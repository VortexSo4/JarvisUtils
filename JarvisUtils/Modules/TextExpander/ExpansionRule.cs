using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace JarvisUtils.Modules.TextExpander;

/// <summary>
/// Правило сокращения. Новые свойства имеют значения по умолчанию, совпадающие со старым
/// поведением: файл настроек со старыми правилами (только TriggerPattern/ReplacementTemplate)
/// читается без изменений смысла.
/// </summary>
public sealed class ExpansionRule
{
    public string TriggerPattern { get; set; } = "";      // например ":rand{n}-{n}:" или старое ":rand%1-%2:"
    public string ReplacementTemplate { get; set; } = ""; // например "{rand(%1,%2)}"

    /// <summary>Необязательное описание — для поиска и подписи в попапах выбора.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Name { get; set; }

    public bool Enabled { get; set; } = true;

    /// <summary>Срабатывать только если триггер начинается с начала слова (перед ним не буква/цифра).</summary>
    public bool WordStart { get; set; }

    /// <summary>Без учёта регистра + перенос регистра на результат (:Tm: → Заглавная, :TM: → ВСЕ).</summary>
    public bool CaseAware { get; set; }

    /// <summary>Только в этих приложениях (имена процессов без .exe). Пусто — везде.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? Apps { get; set; }

    /// <summary>Кроме этих приложений.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? ExcludeApps { get; set; }
}
