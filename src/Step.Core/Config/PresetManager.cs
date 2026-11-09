namespace Step.Core.Config;

using Newtonsoft.Json;
using Step.Core.Models;

/// <summary>
/// 预设管理器
/// 负责加载、保存和切换音频预设
/// </summary>
public class PresetManager
{
    /// <summary>
    /// 出厂内置预设的 ID（旧版预设文件没有 isBuiltIn 字段时按此判断）
    /// </summary>
    private static readonly HashSet<string> BuiltInPresetIds = new(StringComparer.OrdinalIgnoreCase)
    {
        "gaming", "movie", "competitive",
        "vocal_male", "vocal_female", "ballad_rnb", "rock_indie", "metal", "dance_latin", "symphonic", "acoustic_acg",
        "flat"
    };

    /// <summary>
    /// 已废弃的内置预设（加载时移除，避免旧文件里残留）
    /// </summary>
    private static readonly HashSet<string> ObsoleteBuiltInPresetIds = new(StringComparer.OrdinalIgnoreCase)
    {
        "music", "tuhi", "vocal_pop"
    };

    /// <summary>
    /// 内置预设的显示顺序（新的音乐细分放在影音之后、平坦之前）
    /// </summary>
    private static readonly string[] BuiltInOrder =
    {
        "gaming", "movie", "competitive",
        "vocal_male", "vocal_female", "ballad_rnb", "rock_indie", "metal", "dance_latin", "symphonic", "acoustic_acg",
        "flat"
    };

    /// <summary>
    /// 预设字典
    /// </summary>
    private readonly Dictionary<string, Preset> _presets = new();

    /// <summary>
    /// 当前活跃预设
    /// </summary>
    private Preset? _currentPreset;

    /// <summary>
    /// 预设文件路径
    /// </summary>
    private readonly string _presetFilePath;

    /// <summary>
    /// 预设改变事件
    /// </summary>
    public event EventHandler<PresetChangedEventArgs>? PresetChanged;

    public PresetManager(string presetFilePath)
    {
        _presetFilePath = presetFilePath;
    }

    /// <summary>
    /// 从 JSON 文件加载预设
    /// </summary>
    public async Task LoadPresetsAsync()
    {
        try
        {
            if (!File.Exists(_presetFilePath))
            {
                // 首次运行：写入内置默认预设，然后继续加载
                var dir = Path.GetDirectoryName(_presetFilePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);
                await File.WriteAllTextAsync(_presetFilePath, GetDefaultPresetsJson());
            }

            string json = await File.ReadAllTextAsync(_presetFilePath);
            var presetData = JsonConvert.DeserializeObject<PresetData>(json);

            if (presetData?.Presets == null)
            {
                throw new InvalidOperationException("预设文件格式错误");
            }

            _presets.Clear();
            bool removedObsoletePreset = false;

            foreach (var presetJson in presetData.Presets)
            {
                // 旧的“流行音乐”和已删除的“土嗨”在加载时移除
                if (ObsoleteBuiltInPresetIds.Contains(presetJson.Id) &&
                    (presetJson.IsBuiltIn ?? true))
                {
                    removedObsoletePreset = true;
                    continue;
                }

                _presets[presetJson.Id] = CreatePreset(presetJson);
            }

            // 合并/刷新内置预设：新增音乐分类、更新描述和参数
            bool changed = EnsureBuiltInPresets();

            if (removedObsoletePreset)
                changed = true;

            if (changed)
                await SavePresetsAsync();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("加载预设失败", ex);
        }
    }

    private static Preset CreatePreset(PresetJson json)
    {
        return new Preset
        {
            Id = json.Id,
            Name = json.Name,
            Description = json.Description,
            EQBands = NormalizeArray(json.EqBands, new float[15]),
            EQFrequencies = NormalizeArray(json.EqFrequencies, DefaultFrequencies),
            EQQValues = NormalizeArray(json.EqQValues, DefaultQValues),
            UsePrecisionEq = json.UsePrecisionEq,
            LowShelfFrequency = json.LowShelfFrequency,
            LowShelfGain = json.LowShelfGain,
            LowShelfQ = json.LowShelfQ,
            HighShelfFrequency = json.HighShelfFrequency,
            HighShelfGain = json.HighShelfGain,
            HighShelfQ = json.HighShelfQ,
            HRTFIntensity = json.HrtfIntensity,
            SurroundWidth = json.SurroundWidth,
            BassBoost = json.BassBoost,
            IsFavorite = json.IsFavorite,
            // 旧文件没有该字段：按出厂 ID 迁移
            IsBuiltIn = json.IsBuiltIn ?? BuiltInPresetIds.Contains(json.Id)
        };
    }

    private static readonly float[] DefaultFrequencies =
        { 32, 64, 125, 250, 500, 1000, 2000, 4000, 8000, 16000, 2500, 6300, 10000, 12500, 16000 };
    private static readonly float[] DefaultQValues =
        { 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1 };

    private static float[] NormalizeArray(float[]? source, float[] fallback)
    {
        if (source is not { Length: 10 or 15 })
            return (float[])fallback.Clone();

        var result = (float[])fallback.Clone();
        Array.Copy(source, result, source.Length);
        return result;
    }

    /// <summary>
    /// 把默认 JSON 里的内置预设合并进当前列表：缺失的新增，已有的刷新为出厂值。
    /// 内置预设只读，因此这样不会覆盖用户自己的调整。
    /// </summary>
    private bool EnsureBuiltInPresets()
    {
        var defaults = JsonConvert.DeserializeObject<PresetData>(GetDefaultPresetsJson())
            ?? throw new InvalidOperationException("默认预设格式错误");

        bool changed = false;

        foreach (var definition in defaults.Presets)
        {
            if (definition.IsBuiltIn == true)
            {
                if (_presets.TryGetValue(definition.Id, out var existing))
                    changed |= ApplyBuiltIn(existing, definition);
                else
                {
                    _presets[definition.Id] = CreatePreset(definition);
                    changed = true;
                }
            }
            else if (!_presets.ContainsKey(definition.Id))
            {
                _presets[definition.Id] = CreatePreset(definition);
                changed = true;
            }
        }

        return changed;
    }

    private static bool ApplyBuiltIn(Preset target, PresetJson source)
    {
        bool changed = false;

        if (target.Name != source.Name) { target.Name = source.Name; changed = true; }
        if (target.Description != source.Description) { target.Description = source.Description; changed = true; }
        if (target.HRTFIntensity != source.HrtfIntensity) { target.HRTFIntensity = source.HrtfIntensity; changed = true; }
        if (target.SurroundWidth != source.SurroundWidth) { target.SurroundWidth = source.SurroundWidth; changed = true; }
        if (target.BassBoost != source.BassBoost) { target.BassBoost = source.BassBoost; changed = true; }
        if (target.UsePrecisionEq) { target.UsePrecisionEq = false; changed = true; }
        if (!target.IsBuiltIn) { target.IsBuiltIn = true; changed = true; }

        var bands = NormalizeArray(source.EqBands, new float[15]);
        var frequencies = NormalizeArray(source.EqFrequencies, DefaultFrequencies);
        var qValues = NormalizeArray(source.EqQValues, DefaultQValues);

        if (!target.EQBands.SequenceEqual(bands))
        {
            target.EQBands = bands;
            changed = true;
        }

        if (target.EQFrequencies == null || !target.EQFrequencies.SequenceEqual(frequencies))
        {
            target.EQFrequencies = frequencies;
            changed = true;
        }

        if (target.EQQValues == null || !target.EQQValues.SequenceEqual(qValues))
        {
            target.EQQValues = qValues;
            changed = true;
        }

        if (Math.Abs(target.LowShelfFrequency - source.LowShelfFrequency) > 0.01f)
        { target.LowShelfFrequency = source.LowShelfFrequency; changed = true; }
        if (Math.Abs(target.LowShelfGain - source.LowShelfGain) > 0.01f)
        { target.LowShelfGain = source.LowShelfGain; changed = true; }
        if (Math.Abs(target.LowShelfQ - source.LowShelfQ) > 0.001f)
        { target.LowShelfQ = source.LowShelfQ; changed = true; }
        if (Math.Abs(target.HighShelfFrequency - source.HighShelfFrequency) > 0.01f)
        { target.HighShelfFrequency = source.HighShelfFrequency; changed = true; }
        if (Math.Abs(target.HighShelfGain - source.HighShelfGain) > 0.01f)
        { target.HighShelfGain = source.HighShelfGain; changed = true; }
        if (Math.Abs(target.HighShelfQ - source.HighShelfQ) > 0.001f)
        { target.HighShelfQ = source.HighShelfQ; changed = true; }

        return changed;
    }

    /// <summary>
    /// 获取所有预设（内置按固定顺序，自定义追加在后）
    /// </summary>
    public IEnumerable<Preset> GetAllPresets()
    {
        foreach (var id in BuiltInOrder)
        {
            if (_presets.TryGetValue(id, out var preset))
                yield return preset;
        }

        foreach (var preset in _presets.Values)
        {
            if (!BuiltInPresetIds.Contains(preset.Id))
                yield return preset;
        }
    }

    /// <summary>
    /// 获取指定 ID 的预设
    /// </summary>
    public Preset? GetPreset(string id)
    {
        _presets.TryGetValue(id, out var preset);
        return preset;
    }

    /// <summary>
    /// 获取指定名称的预设
    /// </summary>
    public Preset? GetPresetByName(string name)
    {
        return _presets.Values.FirstOrDefault(p => p.Name == name);
    }

    /// <summary>
    /// 切换到指定预设
    /// </summary>
    public void SelectPreset(string id)
    {
        if (!_presets.TryGetValue(id, out var preset))
        {
            throw new KeyNotFoundException($"预设不存在: {id}");
        }

        _currentPreset = preset;
        PresetChanged?.Invoke(this, new PresetChangedEventArgs(preset));
    }

    /// <summary>
    /// 获取当前预设
    /// </summary>
    public Preset? GetCurrentPreset() => _currentPreset;

    /// <summary>
    /// 添加自定义预设
    /// </summary>
    public void AddCustomPreset(Preset preset)
    {
        if (_presets.ContainsKey(preset.Id))
        {
            throw new InvalidOperationException($"预设 ID 已存在: {preset.Id}");
        }

        _presets[preset.Id] = preset;
    }

    /// <summary>
    /// 更新预设
    /// </summary>
    public void UpdatePreset(string id, Preset preset)
    {
        if (!_presets.ContainsKey(id))
        {
            throw new KeyNotFoundException($"预设不存在: {id}");
        }

        preset.Id = id;
        preset.ModifiedAt = DateTime.Now;
        _presets[id] = preset;

        // 如果是当前预设，发送通知
        if (_currentPreset?.Id == id)
        {
            PresetChanged?.Invoke(this, new PresetChangedEventArgs(preset));
        }
    }

    /// <summary>
    /// 删除预设
    /// </summary>
    public void DeletePreset(string id)
    {
        if (!_presets.Remove(id))
        {
            throw new KeyNotFoundException($"预设不存在: {id}");
        }

        if (_currentPreset?.Id == id)
        {
            _currentPreset = null;
        }
    }

    /// <summary>
    /// 保存预设到文件
    /// </summary>
    public async Task SavePresetsAsync()
    {
        try
        {
            var presetData = new PresetData
            {
                Presets = _presets.Values.Select(p => new PresetJson
                {
                    Id = p.Id,
                    Name = p.Name,
                    Description = p.Description,
                    EqBands = p.EQBands,
                    EqFrequencies = p.EQFrequencies,
                    EqQValues = p.EQQValues,
                    UsePrecisionEq = p.UsePrecisionEq,
                    LowShelfFrequency = p.LowShelfFrequency,
                    LowShelfGain = p.LowShelfGain,
                    LowShelfQ = p.LowShelfQ,
                    HighShelfFrequency = p.HighShelfFrequency,
                    HighShelfGain = p.HighShelfGain,
                    HighShelfQ = p.HighShelfQ,
                    HrtfIntensity = p.HRTFIntensity,
                    SurroundWidth = p.SurroundWidth,
                    BassBoost = p.BassBoost,
                    IsFavorite = p.IsFavorite,
                    IsBuiltIn = p.IsBuiltIn
                }).ToList()
            };

            string json = JsonConvert.SerializeObject(presetData, Formatting.Indented);

            var directory = Path.GetDirectoryName(_presetFilePath);
            if (!Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory!);
            }

            await File.WriteAllTextAsync(_presetFilePath, json);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("保存预设失败", ex);
        }
    }

    /// <summary>
    /// 获取收藏预设
    /// </summary>
    public IEnumerable<Preset> GetFavoritePresets()
    {
        return _presets.Values.Where(p => p.IsFavorite);
    }

    /// <summary>
    /// 设置预设为收藏
    /// </summary>
    public void SetFavorite(string id, bool favorite)
    {
        if (_presets.TryGetValue(id, out var preset))
        {
            preset.IsFavorite = favorite;
        }
    }

    #region 内部类

    private class PresetData
    {
        [JsonProperty("presets")]
        public List<PresetJson> Presets { get; set; } = new();
    }

    private class PresetJson
    {
        [JsonProperty("id")]
        public string Id { get; set; } = string.Empty;

        [JsonProperty("name")]
        public string Name { get; set; } = string.Empty;

        [JsonProperty("description")]
        public string Description { get; set; } = string.Empty;

        [JsonProperty("eqBands")]
        public float[]? EqBands { get; set; }

        [JsonProperty("usePrecisionEq")]
        public bool UsePrecisionEq { get; set; }

        [JsonProperty("eqFrequencies")]
        public float[]? EqFrequencies { get; set; }

        [JsonProperty("eqQValues")]
        public float[]? EqQValues { get; set; }

        [JsonProperty("lowShelfFrequency")]
        public float LowShelfFrequency { get; set; } = 100f;

        [JsonProperty("lowShelfGain")]
        public float LowShelfGain { get; set; }

        [JsonProperty("lowShelfQ")]
        public float LowShelfQ { get; set; } = 0.7f;

        [JsonProperty("highShelfFrequency")]
        public float HighShelfFrequency { get; set; } = 10000f;

        [JsonProperty("highShelfGain")]
        public float HighShelfGain { get; set; }

        [JsonProperty("highShelfQ")]
        public float HighShelfQ { get; set; } = 0.7f;

        [JsonProperty("hrtfIntensity")]
        public float HrtfIntensity { get; set; } = 1.0f;

        [JsonProperty("surroundWidth")]
        public float SurroundWidth { get; set; } = 100f;

        [JsonProperty("bassBoost")]
        public float BassBoost { get; set; } = 1.0f;

        [JsonProperty("isFavorite")]
        public bool IsFavorite { get; set; }

        [JsonProperty("isBuiltIn")]
        public bool? IsBuiltIn { get; set; }
    }

    #endregion

    /// <summary>
    /// 内置默认预设 JSON（首次运行时写入文件）
    /// </summary>
    private static string GetDefaultPresetsJson() => """
    {
      "presets": [
        {"id":"gaming","name":"游戏","description":"综合型：保留低频氛围 + 中高频定位，适合 3A/混合场景","eqBands":[1.0,1.5,1,0,-2,5,6,3,2,0],"eqFrequencies":[32,64,125,250,500,1000,2000,4000,8000,16000],"eqQValues":[1.0,1.1,1.2,1.1,1.0,1.0,0.9,0.9,0.9,0.8],"lowShelfFrequency":90,"lowShelfGain":1.0,"lowShelfQ":0.7,"highShelfFrequency":10000,"highShelfGain":1.5,"highShelfQ":0.7,"hrtfIntensity":1.2,"surroundWidth":120,"bassBoost":1.0,"isFavorite":true,"isBuiltIn":true},
        {"id":"movie","name":"电影","description":"电影型：增强环绕与低频，沉浸感优先","eqBands":[1.0,1.5,0,0,1,3,4,3,2,1],"eqFrequencies":[32,64,125,250,500,1000,2000,4000,8000,16000],"eqQValues":[0.8,0.9,1.0,1.0,0.9,0.9,0.9,0.9,0.8,0.7],"lowShelfFrequency":80,"lowShelfGain":1.5,"lowShelfQ":0.7,"highShelfFrequency":10000,"highShelfGain":2.0,"highShelfQ":0.7,"hrtfIntensity":1.3,"surroundWidth":150,"bassBoost":1.0,"isFavorite":true,"isBuiltIn":true},
        {"id":"competitive","name":"竞技精准","description":"竞技型：削低频、抬中高频，脚步/枪声突出，声场更宽","eqBands":[-3.0,-2.0,-2,1,3,5,4,2,0,-1],"eqFrequencies":[32,64,125,250,500,1000,2000,4000,8000,16000],"eqQValues":[1.1,1.2,1.2,1.1,1.0,1.0,0.9,0.9,0.8,0.7],"lowShelfFrequency":100,"lowShelfGain":-2.0,"lowShelfQ":0.7,"highShelfFrequency":8000,"highShelfGain":2.0,"highShelfQ":0.8,"hrtfIntensity":1.5,"surroundWidth":140,"bassBoost":1.0,"isFavorite":true,"isBuiltIn":true},
        {"id":"vocal_male","name":"男声流行","description":"张国荣/周传雄/告五人；中低温暖、齿音克制","eqBands":[0.5,1.5,3,2,1,2,0,-1,-2,-1],"eqFrequencies":[40,80,160,315,630,1250,2500,5000,10000,16000],"eqQValues":[0.8,1.0,1.4,1.4,1.3,1.2,1.1,1.0,0.9,0.8],"lowShelfFrequency":120,"lowShelfGain":0.5,"lowShelfQ":0.6,"highShelfFrequency":9000,"highShelfGain":-1.0,"highShelfQ":0.6,"hrtfIntensity":0.45,"surroundWidth":60,"bassBoost":1.0,"isFavorite":true,"isBuiltIn":true},
        {"id":"vocal_female","name":"女声流行","description":"莫文蔚/邓丽君/邓紫棋；人声通透、空气感","eqBands":[-1.0,0.0,0,1,2,3,3,2,1,0],"eqFrequencies":[50,100,200,400,800,1600,3150,6300,12500,16000],"eqQValues":[0.8,1.0,1.4,1.4,1.4,1.4,1.2,1.1,1.0,0.8],"lowShelfFrequency":120,"lowShelfGain":-1.0,"lowShelfQ":0.6,"highShelfFrequency":9000,"highShelfGain":1.0,"highShelfQ":0.6,"hrtfIntensity":0.45,"surroundWidth":60,"bassBoost":1.0,"isFavorite":true,"isBuiltIn":true},
        {"id":"ballad_rnb","name":"抒情 R&B","description":"暖厚松弛；例：Stay With Me/Shape of My Heart/水星记","eqBands":[1.0,2.0,1,0,1,2,1,0,-1,0],"eqFrequencies":[32,64,125,250,500,1000,2000,4000,8000,16000],"eqQValues":[0.8,1.0,1.1,1.1,1.1,1.1,1.0,0.9,0.8,0.7],"lowShelfFrequency":100,"lowShelfGain":1.0,"lowShelfQ":0.65,"highShelfFrequency":9000,"highShelfGain":0.5,"highShelfQ":0.6,"hrtfIntensity":0.55,"surroundWidth":70,"bassBoost":1.0,"isFavorite":true,"isBuiltIn":true},
        {"id":"rock_indie","name":"摇滚独立","description":"鼓点有力、吉他颗粒；例：故乡/Yellow/Snow","eqBands":[1.5,2.0,1,0,-1,2,3,2,1,0],"eqFrequencies":[32,64,125,250,500,1000,2000,4000,8000,16000],"eqQValues":[0.9,1.0,1.1,1.0,1.0,1.0,1.0,1.0,0.9,0.8],"lowShelfFrequency":90,"lowShelfGain":1.0,"lowShelfQ":0.7,"highShelfFrequency":9500,"highShelfGain":1.0,"highShelfQ":0.6,"hrtfIntensity":0.7,"surroundWidth":95,"bassBoost":1.0,"isFavorite":true,"isBuiltIn":true},
        {"id":"metal","name":"金属","description":"低频紧、攻击性强；例：I Want My Tears Back/Du hast","eqBands":[0.0,0.0,-2,-2,-1,3,4,3,2,0],"eqFrequencies":[32,64,125,250,500,1000,2000,4000,8000,16000],"eqQValues":[1.0,1.1,1.3,1.3,1.2,1.0,0.9,0.9,0.8,0.7],"lowShelfFrequency":90,"lowShelfGain":0.0,"lowShelfQ":0.8,"highShelfFrequency":8000,"highShelfGain":1.0,"highShelfQ":0.6,"hrtfIntensity":0.6,"surroundWidth":110,"bassBoost":1.0,"isFavorite":true,"isBuiltIn":true},
        {"id":"dance_latin","name":"舞曲拉丁","description":"下潜深、节奏亮；例：Despacito/Good Time","eqBands":[2.0,2.5,0,-1,0,2,3,3,2,1],"eqFrequencies":[32,64,125,250,500,1000,2000,4000,8000,16000],"eqQValues":[0.7,0.9,1.0,1.0,1.0,0.9,0.9,0.9,0.8,0.7],"lowShelfFrequency":80,"lowShelfGain":2.0,"lowShelfQ":0.7,"highShelfFrequency":10000,"highShelfGain":2.0,"highShelfQ":0.7,"hrtfIntensity":0.5,"surroundWidth":85,"bassBoost":1.0,"isFavorite":true,"isBuiltIn":true},
        {"id":"symphonic","name":"交响史诗","description":"西幻交响、大动态；例：Journey of a Lifetime","eqBands":[0.5,0.5,0,0,0,1,2,2,3,3],"eqFrequencies":[32,64,125,250,500,1000,2000,4000,8000,16000],"eqQValues":[0.6,0.7,0.9,0.9,0.9,0.9,0.9,0.9,0.9,0.8],"lowShelfFrequency":90,"lowShelfGain":0.5,"lowShelfQ":0.6,"highShelfFrequency":11000,"highShelfGain":2.0,"highShelfQ":0.7,"hrtfIntensity":0.65,"surroundWidth":130,"bassBoost":1.0,"isFavorite":true,"isBuiltIn":true},
        {"id":"acoustic_acg","name":"原声 ACG","description":"指弹与日系；例：流行的云/Bad Angle","eqBands":[-1.0,-1.0,0,0,0,1,2,1,1,1],"eqFrequencies":[40,80,160,320,640,1280,2500,5000,10000,16000],"eqQValues":[0.7,0.8,0.8,0.9,0.9,0.9,0.9,0.9,0.9,0.8],"lowShelfFrequency":120,"lowShelfGain":-0.5,"lowShelfQ":0.6,"highShelfFrequency":9000,"highShelfGain":0.5,"highShelfQ":0.6,"hrtfIntensity":0.35,"surroundWidth":55,"bassBoost":1.0,"isFavorite":true,"isBuiltIn":true},
        {"id":"flat","name":"平坦","description":"参考型：全频段平直，不做调味","eqBands":[0.0,0.0,0,0,0,0,0,0,0,0],"eqFrequencies":[32,64,125,250,500,1000,2000,4000,8000,16000],"eqQValues":[1,1,1,1,1,1,1,1,1,1],"lowShelfFrequency":100,"lowShelfGain":0.0,"lowShelfQ":0.7,"highShelfFrequency":10000,"highShelfGain":0.0,"highShelfQ":0.7,"hrtfIntensity":1.0,"surroundWidth":100,"bassBoost":1.0,"isFavorite":false,"isBuiltIn":true},
        {"id":"custom","name":"自定义","description":"个人定制预设","eqBands":[0.0,0.0,0,0,0,0,0,0,0,0],"eqFrequencies":[32,64,125,250,500,1000,2000,4000,8000,16000],"eqQValues":[1,1,1,1,1,1,1,1,1,1],"lowShelfFrequency":100,"lowShelfGain":0.0,"lowShelfQ":0.7,"highShelfFrequency":10000,"highShelfGain":0.0,"highShelfQ":0.7,"hrtfIntensity":1.0,"surroundWidth":100,"bassBoost":1.0,"isFavorite":false,"isBuiltIn":false}
      ]
    }
    """;
}

/// <summary>
/// 预设改变事件参数
/// </summary>
public class PresetChangedEventArgs : EventArgs
{
    public Preset Preset { get; }
    public PresetChangedEventArgs(Preset preset) => Preset = preset;
}
