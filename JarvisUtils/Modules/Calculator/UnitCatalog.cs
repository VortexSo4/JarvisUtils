using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace JarvisUtils.Modules.Calculator;

public enum Dimension { Length, Mass, Time, Data, Speed, Area, Volume, Temperature, Currency }

/// <summary>
/// Единица измерения: значение в базовой единице величины = v·Factor + Offset
/// (Offset нужен только температуре). У валют Factor не используется — курс берётся из CurrencyRates.
/// </summary>
public sealed record UnitDef(string Symbol, Dimension Dim, double Factor, double Offset = 0)
{
    public double ToBase(double v) => v * Factor + Offset;
    public double FromBase(double b) => (b - Offset) / Factor;
}

/// <summary>
/// Справочник единиц и их написаний (английские, русские, символы валют).
/// Одно написание может означать разные единицы («с» — секунда или °C, «фунт» — масса или GBP):
/// Resolve отдаёт всех кандидатов, а пару «откуда/куда» подбирает CalcEngine по совпадению величины.
/// </summary>
public static class UnitCatalog
{
    private static readonly Dictionary<string, List<UnitDef>> Aliases = new(StringComparer.Ordinal);

    /// <summary>Распространённые ISO-коды: узнаём их и до загрузки курсов (иначе «100 usd» до первой сети — «неизвестная единица»).</summary>
    private static readonly HashSet<string> KnownCurrencyCodes =
    [
        "USD", "EUR", "RUB", "GBP", "JPY", "CNY", "CHF", "CAD", "AUD", "NZD", "SEK", "NOK", "DKK", "PLN",
        "CZK", "HUF", "UAH", "BYN", "KZT", "TRY", "INR", "BRL", "MXN", "KRW", "SGD", "HKD", "AED", "ILS",
        "THB", "GEL", "AMD", "AZN", "UZS", "KGS", "TJS", "RSD", "BGN", "RON", "ZAR", "IDR", "VND", "MYR",
        "PHP", "EGP", "SAR", "QAR", "ISK", "MDL", "ARS", "CLP", "COP", "TMT", "MNT", "VES"
    ];

    // символы валют, которые пишут перед числом: «$100», «€ 50»
    public const string PrefixCurrencySymbols = "$€₽£¥₴₸₺₹₩₾₪";

    static UnitCatalog()
    {
        // ── длина, база — метр ──
        Unit("mm", Dimension.Length, 0.001, "mm", "millimeter", "millimeters", "millimetre", "millimetres", "мм", "миллиметр", "миллиметра", "миллиметров");
        Unit("cm", Dimension.Length, 0.01, "cm", "centimeter", "centimeters", "centimetre", "centimetres", "см", "сантиметр", "сантиметра", "сантиметров");
        Unit("dm", Dimension.Length, 0.1, "dm", "decimeter", "decimeters", "дм", "дециметр", "дециметра", "дециметров");
        Unit("m", Dimension.Length, 1, "m", "meter", "meters", "metre", "metres", "м", "метр", "метра", "метров");
        Unit("km", Dimension.Length, 1000, "km", "kilometer", "kilometers", "kilometre", "kilometres", "км", "километр", "километра", "километров");
        Unit("µm", Dimension.Length, 1e-6, "um", "µm", "μm", "micron", "microns", "мкм", "микрон", "микрона", "микронов");
        Unit("nm", Dimension.Length, 1e-9, "nm", "nanometer", "nanometers", "нм", "нанометр", "нанометра", "нанометров");
        Unit("in", Dimension.Length, 0.0254, "in", "inch", "inches", "\"", "дюйм", "дюйма", "дюймов");
        Unit("ft", Dimension.Length, 0.3048, "ft", "foot", "feet", "'", "фут", "фута", "футов");
        Unit("yd", Dimension.Length, 0.9144, "yd", "yard", "yards", "ярд", "ярда", "ярдов");
        Unit("mi", Dimension.Length, 1609.344, "mi", "mile", "miles", "миля", "мили", "миль", "милю");
        Unit("nmi", Dimension.Length, 1852, "nmi", "nautical mile", "nautical miles", "морская миля", "морские мили", "морских миль");

        // ── масса, база — килограмм ──
        Unit("mg", Dimension.Mass, 1e-6, "mg", "milligram", "milligrams", "мг", "миллиграмм", "миллиграмма", "миллиграммов");
        Unit("g", Dimension.Mass, 0.001, "g", "gram", "grams", "gramme", "grammes", "г", "гр", "грамм", "грамма", "граммов");
        Unit("kg", Dimension.Mass, 1, "kg", "kgs", "kilo", "kilos", "kilogram", "kilograms", "кг", "кило", "килограмм", "килограмма", "килограммов");
        Unit("t", Dimension.Mass, 1000, "t", "ton", "tons", "tonne", "tonnes", "т", "тонна", "тонны", "тонн", "тонну");
        Unit("lb", Dimension.Mass, 0.45359237, "lb", "lbs", "pound", "pounds", "фунт", "фунта", "фунтов");
        Unit("oz", Dimension.Mass, 0.028349523125, "oz", "ounce", "ounces", "унция", "унции", "унций", "унцию");
        Unit("st", Dimension.Mass, 6.35029318, "st", "stone", "stones");
        Unit("ct", Dimension.Mass, 0.0002, "ct", "carat", "carats", "карат", "карата", "каратов");

        // ── время, база — секунда (месяц и год — средние григорианские) ──
        Unit("µs", Dimension.Time, 1e-6, "us", "µs", "μs", "microsecond", "microseconds", "мкс");
        Unit("ms", Dimension.Time, 0.001, "ms", "millisecond", "milliseconds", "мс", "миллисекунда", "миллисекунды", "миллисекунд");
        Unit("s", Dimension.Time, 1, "s", "sec", "secs", "second", "seconds", "с", "сек", "секунда", "секунды", "секунд", "секунду");
        Unit("min", Dimension.Time, 60, "min", "mins", "minute", "minutes", "мин", "минута", "минуты", "минут", "минуту");
        Unit("h", Dimension.Time, 3600, "h", "hr", "hrs", "hour", "hours", "ч", "час", "часа", "часов");
        Unit("d", Dimension.Time, 86400, "d", "day", "days", "д", "дн", "день", "дня", "дней", "сут", "сутки", "суток");
        Unit("wk", Dimension.Time, 604800, "wk", "week", "weeks", "нед", "неделя", "недели", "недель", "неделю");
        Unit("mo", Dimension.Time, 2629746, "mo", "month", "months", "мес", "месяц", "месяца", "месяцев");
        Unit("yr", Dimension.Time, 31556952, "y", "yr", "yrs", "year", "years", "год", "года", "лет");

        // ── данные, база — байт. KB/MB/GB — десятичные (SI), KiB/MiB/GiB — двоичные ──
        Unit("bit", Dimension.Data, 0.125, "bit", "bits", "бит", "бита", "битов");
        Unit("B", Dimension.Data, 1, "b", "byte", "bytes", "б", "байт", "байта", "байтов");
        Unit("KB", Dimension.Data, 1e3, "kb", "kilobyte", "kilobytes", "кб", "кбайт", "килобайт", "килобайта", "килобайтов");
        Unit("MB", Dimension.Data, 1e6, "mb", "megabyte", "megabytes", "мб", "мбайт", "мегабайт", "мегабайта", "мегабайтов");
        Unit("GB", Dimension.Data, 1e9, "gb", "gigabyte", "gigabytes", "гб", "гбайт", "гигабайт", "гигабайта", "гигабайтов");
        Unit("TB", Dimension.Data, 1e12, "tb", "terabyte", "terabytes", "тб", "тбайт", "терабайт", "терабайта", "терабайтов");
        Unit("PB", Dimension.Data, 1e15, "pb", "petabyte", "petabytes", "пб", "петабайт", "петабайта", "петабайтов");
        Unit("KiB", Dimension.Data, 1024, "kib", "kibibyte", "kibibytes", "кибибайт");
        Unit("MiB", Dimension.Data, 1048576, "mib", "mebibyte", "mebibytes", "мебибайт");
        Unit("GiB", Dimension.Data, 1073741824, "gib", "gibibyte", "gibibytes", "гибибайт");
        Unit("TiB", Dimension.Data, 1099511627776, "tib", "tebibyte", "tebibytes", "тебибайт");
        Unit("kbit", Dimension.Data, 125, "kbit", "kbits", "кбит");
        Unit("Mbit", Dimension.Data, 125_000, "mbit", "mbits", "мбит");
        Unit("Gbit", Dimension.Data, 125_000_000, "gbit", "gbits", "гбит");

        // ── скорость, база — м/с ──
        Unit("m/s", Dimension.Speed, 1, "m/s", "mps", "м/с");
        Unit("km/h", Dimension.Speed, 1 / 3.6, "km/h", "kmh", "kph", "kmph", "км/ч", "км/час");
        Unit("mph", Dimension.Speed, 0.44704, "mph", "mi/h", "миль/ч", "миль/час");
        Unit("kn", Dimension.Speed, 1852.0 / 3600, "kn", "kt", "knot", "knots", "узел", "узла", "узлов");
        Unit("ft/s", Dimension.Speed, 0.3048, "ft/s", "fps");

        // ── площадь, база — м² («кв м», «sq ft», «m^2» нормализуются в «м2», «ft2», «m2») ──
        Unit("mm²", Dimension.Area, 1e-6, "mm2", "мм2");
        Unit("cm²", Dimension.Area, 1e-4, "cm2", "см2");
        Unit("m²", Dimension.Area, 1, "m2", "м2", "sqm");
        Unit("km²", Dimension.Area, 1e6, "km2", "км2");
        Unit("ha", Dimension.Area, 1e4, "ha", "hectare", "hectares", "га", "гектар", "гектара", "гектаров");
        Unit("сот.", Dimension.Area, 100, "сотка", "сотки", "соток", "сотку", "ар", "ара", "аров");
        Unit("in²", Dimension.Area, 0.00064516, "in2", "sqin");
        Unit("ft²", Dimension.Area, 0.09290304, "ft2", "sqft");
        Unit("yd²", Dimension.Area, 0.83612736, "yd2");
        Unit("mi²", Dimension.Area, 2589988.110336, "mi2");
        Unit("acre", Dimension.Area, 4046.8564224, "ac", "acre", "acres", "акр", "акра", "акров");

        // ── объём, база — литр (галлоны, пинты, чашки — американские) ──
        Unit("ml", Dimension.Volume, 0.001, "ml", "milliliter", "milliliters", "millilitre", "millilitres", "мл", "миллилитр", "миллилитра", "миллилитров");
        Unit("cl", Dimension.Volume, 0.01, "cl", "сл");
        Unit("dl", Dimension.Volume, 0.1, "dl", "дл");
        Unit("l", Dimension.Volume, 1, "l", "liter", "liters", "litre", "litres", "л", "литр", "литра", "литров");
        Unit("m³", Dimension.Volume, 1000, "m3", "м3", "кубометр", "кубометра", "кубометров");
        Unit("cm³", Dimension.Volume, 0.001, "cm3", "cc", "см3");
        Unit("mm³", Dimension.Volume, 1e-6, "mm3", "мм3");
        Unit("in³", Dimension.Volume, 0.016387064, "in3");
        Unit("ft³", Dimension.Volume, 28.316846592, "ft3");
        Unit("gal", Dimension.Volume, 3.785411784, "gal", "gallon", "gallons", "галлон", "галлона", "галлонов");
        Unit("qt", Dimension.Volume, 0.946352946, "qt", "quart", "quarts", "кварта", "кварты", "кварт");
        Unit("pt", Dimension.Volume, 0.473176473, "pt", "pint", "pints", "пинта", "пинты", "пинт");
        Unit("cup", Dimension.Volume, 0.2365882365, "cup", "cups");
        Unit("fl oz", Dimension.Volume, 0.0295735295625, "floz", "fl oz", "fl.oz");
        Unit("tbsp", Dimension.Volume, 0.01478676478125, "tbsp");
        Unit("tsp", Dimension.Volume, 0.00492892159375, "tsp");

        // ── температура, база — кельвин. «с»/«к» по-русски — ещё и секунда/…: выбор по паре ──
        Unit("°C", Dimension.Temperature, 1, 273.15, "c", "celsius", "цельсий", "цельсия", "с");
        Unit("°F", Dimension.Temperature, 5.0 / 9, 273.15 - 32 * 5.0 / 9, "f", "fahrenheit", "фаренгейт", "фаренгейта", "ф");
        Unit("K", Dimension.Temperature, 1, 0, "k", "kelvin", "кельвин", "кельвина", "кельвинов", "к");

        // ── валюты: символы и словесные названия; ISO-коды — в KnownCurrencyCodes/курсах ──
        Currency("USD", "$", "us$", "dollar", "dollars", "доллар", "доллара", "долларов", "долл", "бакс", "бакса", "баксов");
        Currency("EUR", "€", "euro", "euros", "евро");
        Currency("RUB", "₽", "руб", "рубль", "рубля", "рублей", "р", "rur", "ruble", "rubles", "rouble", "roubles");
        Currency("GBP", "£", "pound", "pounds", "фунт", "фунта", "фунтов", "фунт стерлингов", "фунтов стерлингов");
        Currency("JPY", "¥", "yen", "иена", "иены", "иен", "йена", "йены", "йен");
        Currency("CNY", "yuan", "rmb", "юань", "юаня", "юаней");
        Currency("UAH", "₴", "грн", "гривна", "гривны", "гривен");
        Currency("KZT", "₸", "тенге");
        Currency("TRY", "₺", "лира", "лиры", "лир");
        Currency("CHF", "франк", "франка", "франков");
        Currency("INR", "₹", "рупия", "рупии", "рупий");
        Currency("KRW", "₩", "вона", "воны", "вон");
        Currency("GEL", "₾", "лари");
        Currency("ILS", "₪", "шекель", "шекеля", "шекелей");
        Currency("PLN", "zł", "злотый", "злотых");
        Currency("BYN", "бел руб", "белорусский рубль", "белорусских рублей");
    }

    private static void Unit(string symbol, Dimension dim, double factor, params string[] aliases) =>
        Unit(symbol, dim, factor, 0, aliases);

    private static void Unit(string symbol, Dimension dim, double factor, double offset, params string[] aliases)
    {
        var def = new UnitDef(symbol, dim, factor, offset);
        foreach (var a in aliases) AddAlias(Normalize(a), def);
    }

    private static void Currency(string code, params string[] aliases)
    {
        var def = CurrencyUnit(code);
        foreach (var a in aliases) AddAlias(Normalize(a), def);
    }

    private static void AddAlias(string key, UnitDef def)
    {
        if (!Aliases.TryGetValue(key, out var list)) Aliases[key] = list = [];
        if (!list.Contains(def)) list.Add(def);
    }

    private static UnitDef CurrencyUnit(string code) => new(code, Dimension.Currency, 1);

    /// <summary>
    /// Все единицы, которыми может быть написанное (порядок — приоритет). Пусто — не единица.
    /// knownCurrency — коды из загруженных курсов (кроме встроенного списка), может быть null.
    /// </summary>
    public static IReadOnlyList<UnitDef> Resolve(string text, Func<string, bool>? knownCurrency = null)
    {
        bool degree = text.Contains('°');
        string key = Normalize(text);
        if (key.Length == 0) return [];

        var result = new List<UnitDef>();
        if (Aliases.TryGetValue(key, out var list)) result.AddRange(list);

        // трёхбуквенный ISO-код: «usd», «EUR», «czk»
        if (key.Length == 3 && key.All(c => c is >= 'a' and <= 'z'))
        {
            string code = key.ToUpperInvariant();
            if (KnownCurrencyCodes.Contains(code) || knownCurrency?.Invoke(code) == true)
            {
                var cur = CurrencyUnit(code);
                if (!result.Contains(cur)) result.Add(cur);
            }
        }

        // «°с» — точно температура, а не секунда
        if (degree) result.RemoveAll(u => u.Dim != Dimension.Temperature);
        return result;
    }

    private static readonly Regex Spaces = new(@"\s+", RegexOptions.Compiled);
    // разделитель после префикса обязателен: иначе «кварта» → «арта2», «cup» → «p3»
    private static readonly Regex SquarePrefix = new(@"^(?:sq|square|кв)(?:\s+|\.\s*)(.+)$", RegexOptions.Compiled);
    private static readonly Regex CubicPrefix = new(@"^(?:cu|cubic|куб)(?:\s+|\.\s*)(.+)$", RegexOptions.Compiled);

    /// <summary>
    /// Ключ для поиска: регистр, «ё», степени (m² / m^2 / sq m / кв.м → m2 / м2), градусы,
    /// пробелы вокруг «/» и точка в конце сокращения («руб.», «сек.») не важны.
    /// </summary>
    public static string Normalize(string text)
    {
        string s = text.Trim().ToLowerInvariant().Replace('ё', 'е')
            .Replace("²", "2").Replace("³", "3").Replace("^2", "2").Replace("^3", "3")
            .Replace("°", "");
        s = Spaces.Replace(s, " ").Replace(" /", "/").Replace("/ ", "/").Trim();
        if (s.Length > 1 && s.EndsWith('.')) s = s[..^1];

        var sq = SquarePrefix.Match(s);
        if (sq.Success && !sq.Groups[1].Value.EndsWith('2')) return sq.Groups[1].Value.Trim() + "2";
        var cu = CubicPrefix.Match(s);
        if (cu.Success && !cu.Groups[1].Value.EndsWith('3')) return cu.Groups[1].Value.Trim() + "3";
        return s;
    }
}
