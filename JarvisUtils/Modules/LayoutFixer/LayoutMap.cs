using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace JarvisUtils.Modules.LayoutFixer;

public static class LayoutMap
{
    private static readonly Dictionary<char, char> RuToEn = Build();

    // Пары "русская раскладка (нижн+верхн) — английская (нижн+верхн)": хХ ↔ [ {
    private static Dictionary<char, char> Build()
    {
        var map = new Dictionary<char, char>();
        void Add(string ru, string en) { map[ru[0]] = en[0]; map[ru[1]] = en[1]; }

        Add("ёЁ", "`~");
        Add("йЙ", "qQ"); Add("цЦ", "wW"); Add("уУ", "eE"); Add("кК", "rR"); Add("еЕ", "tT");
        Add("нН", "yY"); Add("гГ", "uU"); Add("шШ", "iI"); Add("щЩ", "oO"); Add("зЗ", "pP");
        Add("хХ", "[{"); Add("ъЪ", "]}");
        Add("фФ", "aA"); Add("ыЫ", "sS"); Add("вВ", "dD"); Add("аА", "fF"); Add("пП", "gG");
        Add("рР", "hH"); Add("оО", "jJ"); Add("лЛ", "kK"); Add("дД", "lL");
        Add("жЖ", ";:"); Add("эЭ", "'\"");
        Add("яЯ", "zZ"); Add("чЧ", "xX"); Add("сС", "cC"); Add("мМ", "vV"); Add("иИ", "bB");
        Add("тТ", "nN"); Add("ьЬ", "mM"); Add("бБ", ",<"); Add("юЮ", ".>");

        // верхний цифровой ряд с Shift
        map['"'] = '@'; map['№'] = '#'; map[';'] = '$'; map['?'] = '&';
        map['.'] = '/';
        map[','] = '?';
        return map;
    }

    private static readonly Dictionary<char, char> EnToRu =
        RuToEn.DistinctBy(kv => kv.Value).ToDictionary(kv => kv.Value, kv => kv.Key);

    public static string Convert(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        int ru = 0, en = 0;
        foreach (var c in text)
        {
            if (c is (>= 'а' and <= 'я') or 'ё' or (>= 'А' and <= 'Я') or 'Ё') ru++;
            else if (c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z')) en++;
        }
        var map = ru >= en ? RuToEn : EnToRu;
        return new string(text.Select(c => map.TryGetValue(c, out var m) ? m : c).ToArray());
    }

    /// <summary>
    /// RU→EN только для БУКВ (по физическим клавишам); все прочие символы не трогаются.
    /// Для раскладко-независимого матчинга TextExpander'а: буфер ":уь", набранный в русской
    /// раскладке теми же физическими клавишами, что и ":em", превращается в ":em".
    /// Символы сознательно не конвертируем: ':' в русской раскладке — это Shift+7, и пользователь,
    /// нажавший её, имел в виду именно двоеточие, а не '&' с той же физической клавиши в EN.
    /// (В многопроектной структуре этому методу место в Core — сейчас модули в одной сборке.)
    /// </summary>
    public static string ToEnglish(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        var sb = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            bool cyrillicLetter = c is (>= 'а' and <= 'я') or (>= 'А' and <= 'Я') or 'ё' or 'Ё';
            sb.Append(cyrillicLetter && RuToEn.TryGetValue(c, out var en) ? en : c);
        }
        return sb.ToString();
    }
}