using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text;
using System.Text.Json;

using OmniRead.Localization;

namespace OmniRead.Services;

/// <summary>What a voice's model card says about the voice it was fine-tuned from.</summary>
public enum PiperBase {
    /// <summary>Trained from scratch, or from a base whose recordings are CC0 or public domain.</summary>
    Free,

    /// <summary>Trained from hifi_tts_92, whose recordings are CC BY: credit is required.</summary>
    Attribution,

    /// <summary>Trained from lessac, whose recordings are licensed for research and non-commercial use.</summary>
    Lessac,

    /// <summary>Trained from ryan, whose recordings are licensed for non-commercial use.</summary>
    Ryan,

    /// <summary>Trained from some other voice, whose own card has to be read for its terms.</summary>
    Indirect,

    /// <summary>The card does not say.</summary>
    Unknown
}

/// <summary>A Piper voice the reader offers. The key is Piper's own name, such as "en_US-ljspeech-high".</summary>
public sealed record PiperVoice(string Key, string License, long Bytes, PiperBase Base) {
    private static readonly string[] Qualities = ["x_low", "low", "medium", "high"];

    public string Locale => Key[..Key.IndexOf('-')];

    public string Name => Key[(Key.IndexOf('-') + 1)..Key.LastIndexOf('-')];

    public string Quality => Key[(Key.LastIndexOf('-') + 1)..];

    /// <summary>Position from lowest to highest quality.</summary>
    public int QualityRank => Array.IndexOf(Qualities, Quality);

    public string ShortName => $"{Name} ({Quality.Replace('_', '-')})";

    public string DisplayName => $"{LanguageName} · {ShortName}";

    public string Folder => Path.Combine(Piper.VoicesFolder, Key);

    public string ModelPath => Path.Combine(Folder, Key + ".onnx");

    public string CardPath => Path.Combine(Folder, "MODEL_CARD");

    public bool IsInstalled => File.Exists(ModelPath) && File.Exists(ModelPath + ".json") && File.Exists(CardPath);

    /// <summary>Where the voice's files live in the piper-voices repository on Hugging Face.</summary>
    public string BaseAddress => $"https://huggingface.co/rhasspy/piper-voices/resolve/main/{Locale[..Locale.IndexOf('_')]}/{Locale}/{Name}/{Quality}/";

    public string LanguageName {
        get {
            try {
                return CultureInfo.GetCultureInfo(Locale.Replace('_', '-')).NativeName;
            }
            catch (CultureNotFoundException) {
                return Locale;
            }
        }
    }
}

/// <summary>
/// Runs the Piper neural text-to-speech engine (https://github.com/OHF-Voice/piper1-gpl) as a separate program.
/// </summary>
/// <remarks>
/// Piper is GPL-3.0-or-later and is not part of OmniRead: when asked, the reader downloads the official Python
/// package into its data folder and talks to it over standard input and output. Every voice is offered, with
/// the licence of its recordings and of the voice it was trained from shown beside it; many are limited to
/// non-commercial or research use. Each voice's MODEL_CARD is downloaded and kept with it.
/// </remarks>
public static class Piper {
    public const string Version = "1.8.0";
    public const string VoicePrefix = "piper:";

    private const string PythonVersion = "3.12.10";
    private const string PythonAddress = $"https://www.python.org/ftp/python/{PythonVersion}/python-{PythonVersion}-embed-amd64.zip";
    private const string PipAddress = "https://bootstrap.pypa.io/get-pip.py";

    // The bridge imports Piper, so this small script is offered under Piper's licence.
    private const string Bridge = """
        # SPDX-License-Identifier: GPL-3.0-or-later
        # Reads one JSON request per line on standard input and answers with one line holding a base64 WAV file.
        import base64
        import io
        import json
        import sys
        import wave

        from piper import PiperVoice, SynthesisConfig

        voice = PiperVoice.load(sys.argv[1])
        sys.stdout.write("READY\n")
        sys.stdout.flush()
        for line in sys.stdin:
            try:
                request = json.loads(line)
                buffer = io.BytesIO()
                with wave.open(buffer, "wb") as wav_file:
                    config = SynthesisConfig(length_scale=request.get("length_scale", 1.0))
                    voice.synthesize_wav(request["text"], wav_file, syn_config=config)
                sys.stdout.write("OK " + base64.b64encode(buffer.getvalue()).decode("ascii") + "\n")
            except Exception as error:
                sys.stdout.write("ERR " + str(error).replace("\n", " ") + "\n")
            sys.stdout.flush()
        """;

    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromMinutes(30) };
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static Process? _process;
    private static string? _processVoice;
    private static string _lastError = "";

    /// <summary>
    /// Every voice in the piper-voices repository, with the licence its MODEL_CARD gives for its recordings and what
    /// the card says about the voice it was trained from. Surveyed in November 2026.
    /// </summary>
    public static IReadOnlyList<PiperVoice> Catalog { get; } = [
        new("ar_JO-kareem-low", "See model card", 63201294, PiperBase.Lessac),
        new("ar_JO-kareem-medium", "See model card", 63201294, PiperBase.Lessac),
        new("bg_BG-dimitar-medium", "CC0", 63221984, PiperBase.Free),
        new("bn_BD-google-medium", "CC BY-SA", 76782515, PiperBase.Attribution),
        new("ca_ES-upc_ona-medium", "CC BY-SA", 63201294, PiperBase.Lessac),
        new("ca_ES-upc_ona-x_low", "CC BY-SA", 20628813, PiperBase.Free),
        new("ca_ES-upc_pau-x_low", "CC BY-SA", 28130791, PiperBase.Free),
        new("cs_CZ-jirka-low", "CC0", 63201294, PiperBase.Lessac),
        new("cs_CZ-jirka-medium", "CC0", 63221984, PiperBase.Attribution),
        new("cs_CZ-kasandra-medium", "CC BY", 63511038, PiperBase.Free),
        new("cy_GB-bu_tts-medium", "CC BY", 77061326, PiperBase.Lessac),
        new("cy_GB-gwryw_gogleddol-medium", "See model card", 63511038, PiperBase.Lessac),
        new("da_DK-talesyntese-medium", "CC0", 63201294, PiperBase.Lessac),
        new("de_DE-eva_k-x_low", "See model card", 20628813, PiperBase.Free),
        new("de_DE-karlsson-low", "See model card", 63104526, PiperBase.Ryan),
        new("de_DE-kerstin-low", "CC0", 63104526, PiperBase.Ryan),
        new("de_DE-kerstin-medium", "CC0", 63221984, PiperBase.Attribution),
        new("de_DE-mls-medium", "CC BY", 76961079, PiperBase.Free),
        new("de_DE-pavoque-low", "CC BY-NC-SA", 63104526, PiperBase.Ryan),
        new("de_DE-ramona-low", "See model card", 63104526, PiperBase.Free),
        new("de_DE-thorsten_emotional-medium", "CC0", 76745905, PiperBase.Indirect),
        new("de_DE-thorsten-high", "CC0", 113895201, PiperBase.Lessac),
        new("de_DE-thorsten-low", "CC0", 63104526, PiperBase.Ryan),
        new("de_DE-thorsten-medium", "CC0", 63221984, PiperBase.Free),
        new("el_GR-joy-medium", "CC BY-NC", 63516050, PiperBase.Free),
        new("el_GR-rapunzelina-low", "CC0", 63104526, PiperBase.Ryan),
        new("el_GR-rapunzelina-medium", "CC0", 62950044, PiperBase.Free),
        new("en_GB-alan-low", "See model card", 63104526, PiperBase.Ryan),
        new("en_GB-alan-medium", "See model card", 63201294, PiperBase.Lessac),
        new("en_GB-alba-medium", "CC BY", 63221984, PiperBase.Attribution),
        new("en_GB-aru-medium", "CC BY", 76774323, PiperBase.Attribution),
        new("en_GB-cori-high", "Public domain", 114219352, PiperBase.Free),
        new("en_GB-cori-medium", "CC BY", 63221984, PiperBase.Attribution),
        new("en_GB-jenny_dioco-medium", "See model card", 63221984, PiperBase.Attribution),
        new("en_GB-northern_english_male-medium", "CC BY-SA", 63201294, PiperBase.Lessac),
        new("en_GB-semaine-medium", "CC BY-NC-SA", 76737711, PiperBase.Lessac),
        new("en_GB-southern_english_female-low", "CC BY-SA", 63104526, PiperBase.Ryan),
        new("en_GB-vctk-medium", "CC BY", 76972979, PiperBase.Attribution),
        new("en_US-amy-low", "See model card", 63104526, PiperBase.Ryan),
        new("en_US-amy-medium", "See model card", 63221984, PiperBase.Attribution),
        new("en_US-arctic-medium", "See model card", 76766385, PiperBase.Lessac),
        new("en_US-bryce-medium", "Public domain", 63531379, PiperBase.Unknown),
        new("en_US-danny-low", "See model card", 63104526, PiperBase.Ryan),
        new("en_US-hfc_female-medium", "CC BY-NC-SA", 63221984, PiperBase.Attribution),
        new("en_US-hfc_male-medium", "CC BY-NC-SA", 63201294, PiperBase.Lessac),
        new("en_US-hifi_tts_92-medium", "CC BY", 63221984, PiperBase.Lessac),
        new("en_US-joe-medium", "CC0", 63221984, PiperBase.Free),
        new("en_US-john-medium", "Public domain", 63531379, PiperBase.Free),
        new("en_US-kathleen-low", "CC0", 63104526, PiperBase.Ryan),
        new("en_US-kathleen-medium", "CC0", 63221984, PiperBase.Attribution),
        new("en_US-kristin-medium", "Public domain", 63531379, PiperBase.Free),
        new("en_US-kusal-medium", "See model card", 63201294, PiperBase.Lessac),
        new("en_US-l2arctic-medium", "CC BY-NC", 76778673, PiperBase.Lessac),
        new("en_US-lessac-high", "Lessac Blizzard (research only)", 113895201, PiperBase.Free),
        new("en_US-lessac-low", "Lessac Blizzard (research only)", 63201294, PiperBase.Free),
        new("en_US-lessac-medium", "Lessac Blizzard (research only)", 63201294, PiperBase.Free),
        new("en_US-libritts_r-medium", "CC BY", 78580914, PiperBase.Lessac),
        new("en_US-libritts-high", "CC BY", 136673811, PiperBase.Free),
        new("en_US-ljspeech-high", "Public domain", 114199011, PiperBase.Free),
        new("en_US-ljspeech-medium", "Public domain", 63531379, PiperBase.Free),
        new("en_US-mike-medium", "CC0", 63221984, PiperBase.Free),
        new("en_US-norman-medium", "Public domain", 63531379, PiperBase.Free),
        new("en_US-reza_ibrahim-medium", "CC0", 63511038, PiperBase.Lessac),
        new("en_US-ryan-high", "CC BY-NC-SA", 120786792, PiperBase.Free),
        new("en_US-ryan-low", "CC BY-NC-SA", 63104526, PiperBase.Free),
        new("en_US-ryan-medium", "CC BY-NC-SA", 63221984, PiperBase.Free),
        new("en_US-sam-medium", "Apache 2.0", 63221984, PiperBase.Free),
        new("es_AR-daniela-high", "CC BY-SA", 114199011, PiperBase.Unknown),
        new("es_ES-carlfm-x_low", "Public domain", 28130791, PiperBase.Free),
        new("es_ES-davefx-medium", "CC0", 63221984, PiperBase.Free),
        new("es_ES-mls_10246-low", "CC BY", 63104526, PiperBase.Ryan),
        new("es_ES-mls_9972-low", "CC BY", 63104526, PiperBase.Ryan),
        new("es_ES-sharvard-medium", "CC BY", 76753841, PiperBase.Attribution),
        new("es_MX-ald-medium", "Unlicense", 63221984, PiperBase.Free),
        new("es_MX-ald-x_low", "See model card", 20986952, PiperBase.Free),
        new("es_MX-claude-high", "Apache 2.0", 63122309, PiperBase.Unknown),
        new("et_EE-news-medium", "CC BY", 76757937, PiperBase.Attribution),
        new("eu_ES-antton-medium", "CC BY", 63221984, PiperBase.Free),
        new("eu_ES-maider-medium", "CC BY", 63221984, PiperBase.Attribution),
        new("fa_IR-amir-medium", "CC0", 63221984, PiperBase.Free),
        new("fa_IR-ganji_adabi-medium", "CC0", 63516050, PiperBase.Indirect),
        new("fa_IR-ganji-medium", "CC0", 63516050, PiperBase.Indirect),
        new("fa_IR-gyro-medium", "See model card", 63122309, PiperBase.Indirect),
        new("fa_IR-magnoliasis-medium", "No documented licence", 63221984, PiperBase.Attribution),
        new("fa_IR-reza_ibrahim-medium", "CC0", 63511038, PiperBase.Lessac),
        new("fi_FI-harri-low", "CC0", 69795191, PiperBase.Ryan),
        new("fi_FI-harri-medium", "CC0", 63221984, PiperBase.Free),
        new("fr_FR-gilles-low", "CC0", 63104526, PiperBase.Ryan),
        new("fr_FR-mls_1840-low", "CC BY", 63104526, PiperBase.Ryan),
        new("fr_FR-mls-medium", "CC BY", 76733750, PiperBase.Free),
        new("fr_FR-siwis-low", "CC BY", 28130791, PiperBase.Ryan),
        new("fr_FR-siwis-medium", "CC BY", 63221984, PiperBase.Attribution),
        new("fr_FR-tom-medium", "AGPL 3", 63511038, PiperBase.Unknown),
        new("fr_FR-upmc-medium", "CC BY-SA", 76733615, PiperBase.Lessac),
        new("he_IL-saspeech-medium", "See model card", 63221984, PiperBase.Free),
        new("hi_IN-pratham-medium", "CC BY-NC-SA", 63516050, PiperBase.Unknown),
        new("hi_IN-priyamvada-medium", "CC BY-NC-SA", 63516050, PiperBase.Unknown),
        new("hi_IN-rohan-medium", "IndicTTS licence", 62950044, PiperBase.Lessac),
        new("hu_HU-anna-medium", "CC0", 63221984, PiperBase.Attribution),
        new("hu_HU-berta-medium", "CC0", 63221984, PiperBase.Attribution),
        new("hu_HU-imre-medium", "CC0", 63221984, PiperBase.Free),
        new("hy_AM-gor-medium", "GPL 2", 63511038, PiperBase.Indirect),
        new("id_ID-news_tts-medium", "See model card", 62950044, PiperBase.Lessac),
        new("is_IS-bui-medium", "See model card", 76495465, PiperBase.Free),
        new("is_IS-salka-medium", "See model card", 76495465, PiperBase.Free),
        new("is_IS-steinn-medium", "See model card", 76495465, PiperBase.Free),
        new("is_IS-ugla-medium", "See model card", 76495465, PiperBase.Free),
        new("it_IT-paola-medium", "CC0", 63221984, PiperBase.Attribution),
        new("it_IT-riccardo-x_low", "See model card", 28130791, PiperBase.Free),
        new("it_IT-serena-high", "CC BY", 114204024, PiperBase.Free),
        new("it_IT-serena-medium", "CC BY", 63516051, PiperBase.Free),
        new("ja_JP-hi_fi_captain-medium", "CC BY-NC-SA", 76753841, PiperBase.Attribution),
        new("ka_GE-natia-medium", "See model card", 63201294, PiperBase.Lessac),
        new("kk_KZ-iseke-x_low", "CC BY", 28130791, PiperBase.Free),
        new("kk_KZ-issai-high", "CC BY", 127864258, PiperBase.Free),
        new("kk_KZ-raya-x_low", "CC BY", 28130791, PiperBase.Free),
        new("ko_KR-kss-medium", "CC BY-NC-SA", 63221984, PiperBase.Attribution),
        new("ku_TR-berfin_renas-medium", "CC BY-NC", 77060307, PiperBase.Unknown),
        new("lb_LU-marylux-medium", "CC BY-NC-SA", 63201294, PiperBase.Lessac),
        new("lt_LT-ingute-medium", "Open RAIL-D", 63516051, PiperBase.Indirect),
        new("lt_LT-reginute1-medium", "CC BY", 63516051, PiperBase.Indirect),
        new("lv_LV-aivars-medium", "CC0", 63511038, PiperBase.Unknown),
        new("ml_IN-arjun-medium", "See model card", 62950044, PiperBase.Lessac),
        new("ml_IN-meera-medium", "See model card", 62950044, PiperBase.Lessac),
        new("mr_IN-google-medium", "CC BY-SA", 76768179, PiperBase.Attribution),
        new("ne_NP-chitwan-medium", "CC0", 63221984, PiperBase.Free),
        new("ne_NP-google-medium", "CC BY-SA", 76766385, PiperBase.Lessac),
        new("ne_NP-google-x_low", "CC BY-SA", 27693157, PiperBase.Free),
        new("nl_BE-flemishguy-medium", "CC0", 63221984, PiperBase.Free),
        new("nl_BE-nathalie-medium", "CC0", 63221984, PiperBase.Attribution),
        new("nl_BE-nathalie-x_low", "CC0", 20628813, PiperBase.Free),
        new("nl_BE-rdh-medium", "CC0", 63221984, PiperBase.Free),
        new("nl_BE-rdh-x_low", "CC0", 20628813, PiperBase.Free),
        new("nl_NL-alex-medium", "CC0", 63221984, PiperBase.Free),
        new("nl_NL-mls_5809-low", "CC BY", 63104526, PiperBase.Ryan),
        new("nl_NL-mls_7432-low", "CC BY", 63104526, PiperBase.Ryan),
        new("nl_NL-mls-medium", "CC BY", 76584246, PiperBase.Free),
        new("nl_NL-pim-medium", "CC0", 63221984, PiperBase.Free),
        new("nl_NL-ronnie-medium", "CC0", 63221984, PiperBase.Free),
        new("no_NO-nvcc-medium", "CC0", 76770227, PiperBase.Free),
        new("no_NO-talesyntese-medium", "CC0", 63221984, PiperBase.Free),
        new("pl_PL-bass-high", "See model card", 114204024, PiperBase.Unknown),
        new("pl_PL-darkman-medium", "CC0", 63221984, PiperBase.Free),
        new("pl_PL-gosia-medium", "CC0", 63221984, PiperBase.Attribution),
        new("pl_PL-mc_speech-medium", "CC0", 63201294, PiperBase.Lessac),
        new("pl_PL-mls_6892-low", "CC BY", 63104526, PiperBase.Ryan),
        new("pt_BR-cadu-medium", "CC0", 63221984, PiperBase.Free),
        new("pt_BR-edresson-low", "CC BY", 63104526, PiperBase.Ryan),
        new("pt_BR-faber-medium", "CC0", 63221984, PiperBase.Attribution),
        new("pt_BR-jeff-medium", "CC0", 63221984, PiperBase.Free),
        new("pt_PT-tugão-medium", "CC0", 63221984, PiperBase.Attribution),
        new("ro_RO-mihai-medium", "CC0", 63221984, PiperBase.Free),
        new("ru_RU-denis-medium", "CC0", 63221984, PiperBase.Free),
        new("ru_RU-dmitri-medium", "CC0", 63221984, PiperBase.Attribution),
        new("ru_RU-irina-medium", "No documented licence", 63201294, PiperBase.Lessac),
        new("ru_RU-ruslan-medium", "CC BY-NC-SA", 63201294, PiperBase.Lessac),
        new("sk_SK-lili-medium", "CC0", 63221984, PiperBase.Attribution),
        new("sl_SI-artur-medium", "CC BY", 63200492, PiperBase.Unknown),
        new("sq_AL-edon-medium", "CC0", 63511038, PiperBase.Lessac),
        new("sr_RS-serbski_institut-medium", "CC BY-NC-SA", 76733615, PiperBase.Lessac),
        new("sv_SE-alma-medium", "See model card", 63434611, PiperBase.Free),
        new("sv_SE-lisa-medium", "See model card", 63511038, PiperBase.Indirect),
        new("sv_SE-nst-medium", "CC0", 63104526, PiperBase.Free),
        new("sw_CD-lanfrica-medium", "See model card", 63221984, PiperBase.Free),
        new("te_IN-maya-medium", "IndicTTS licence", 62950044, PiperBase.Lessac),
        new("te_IN-padmavathi-medium", "CC BY", 63516050, PiperBase.Unknown),
        new("te_IN-venkatesh-medium", "CC BY", 63516050, PiperBase.Unknown),
        new("th_TH-tsync2-medium", "CC BY-NC-SA", 63221984, PiperBase.Attribution),
        new("tr_TR-dfki-medium", "CC BY-NC-SA", 63201294, PiperBase.Lessac),
        new("uk_UA-lada-x_low", "Apache 2.0", 20628813, PiperBase.Free),
        new("uk_UA-mykyta-high", "Apache 2.0", 114204024, PiperBase.Unknown),
        new("uk_UA-oleksa-high", "Apache 2.0", 114204024, PiperBase.Unknown),
        new("uk_UA-tetiana-high", "Apache 2.0", 114204024, PiperBase.Unknown),
        new("uk_UA-ukrainian_tts-medium", "CC0", 76735663, PiperBase.Free),
        new("ur_PK-aegis_female-medium", "MIT", 63515589, PiperBase.Unknown),
        new("ur_PK-fasih-medium", "MIT", 64832023, PiperBase.Unknown),
        new("vi_VN-25hours_single-low", "No documented licence", 63104526, PiperBase.Ryan),
        new("vi_VN-vais1000-medium", "CC BY", 63221984, PiperBase.Attribution),
        new("vi_VN-vivos-x_low", "CC BY-NC-SA", 27789413, PiperBase.Free),
        new("zh_CN-chaowen-medium", "CC0", 63221984, PiperBase.Attribution),
        new("zh_CN-huayan-medium", "No documented licence", 63201294, PiperBase.Lessac),
        new("zh_CN-huayan-x_low", "No documented licence", 20628813, PiperBase.Free),
        new("zh_CN-xiao_ya-medium", "Non-commercial", 63221984, PiperBase.Free)
    ];

    public static string Root => Path.Combine(SettingsStore.DataFolder, "Piper");

    public static string VoicesFolder => Path.Combine(Root, "voices");

    private static string PythonFolder => Path.Combine(Root, "python");

    private static string PythonProgram => Path.Combine(PythonFolder, "python.exe");

    private static string InstalledMarker => Path.Combine(PythonFolder, $"piper-{Version}.installed");

    private static string BridgePath => Path.Combine(Root, "omniread_piper_bridge.py");

    public static bool EngineInstalled => File.Exists(InstalledMarker) && File.Exists(PythonProgram);

    public static IEnumerable<PiperVoice> InstalledVoices => Catalog.Where(static voice => voice.IsInstalled);

    /// <summary>Downloads Python and the Piper package into the data folder. Takes a few minutes.</summary>
    public static async Task InstallEngineAsync(IProgress<string> progress, CancellationToken cancel) {
        Shutdown();
        Directory.CreateDirectory(Root);
        if (!File.Exists(PythonProgram)) {
            progress.Report(Localizer.Get("Piper_Step_Python"));
            string archive = Path.Combine(Root, "python.zip");
            await DownloadAsync(PythonAddress, archive, null, cancel);
            if (Directory.Exists(PythonFolder)) {
                Directory.Delete(PythonFolder, true);
            }
            ZipFile.ExtractToDirectory(archive, PythonFolder);
            File.Delete(archive);
            // The embeddable Python ignores installed packages until its path file says "import site".
            foreach (string pathFile in Directory.EnumerateFiles(PythonFolder, "python*._pth")) {
                File.WriteAllText(pathFile, File.ReadAllText(pathFile).Replace("#import site", "import site"));
            }
        }
        progress.Report(Localizer.Get("Piper_Step_Pip"));
        string installer = Path.Combine(Root, "get-pip.py");
        await DownloadAsync(PipAddress, installer, null, cancel);
        await RunPythonAsync($"\"{installer}\" --no-warn-script-location --disable-pip-version-check", cancel);
        File.Delete(installer);
        progress.Report(Localizer.Get("Piper_Step_Package"));
        await RunPythonAsync($"-m pip install --no-warn-script-location --disable-pip-version-check piper-tts=={Version}", cancel);
        await File.WriteAllTextAsync(InstalledMarker, DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture), cancel);
        progress.Report(Localizer.Get("Piper_Done"));
    }

    /// <summary>Downloads a voice and keeps its model card, the only place its licence is recorded, beside it.</summary>
    public static async Task DownloadVoiceAsync(PiperVoice voice, IProgress<string> progress, CancellationToken cancel) {
        progress.Report(Localizer.Get("Piper_Step_Card"));
        string card;
        try {
            card = await Client.GetStringAsync(voice.BaseAddress + "MODEL_CARD", cancel);
        }
        catch (HttpRequestException) {
            // A few voices were published without one.
            card = "No MODEL_CARD was published for this voice.";
        }
        Directory.CreateDirectory(voice.Folder);
        await DownloadAsync(voice.BaseAddress + voice.Key + ".onnx.json", voice.ModelPath + ".json", null, cancel);
        Progress<int> percent = new(value => progress.Report(Localizer.Format("Piper_Step_Voice", voice.DisplayName, value)));
        await DownloadAsync(voice.BaseAddress + voice.Key + ".onnx", voice.ModelPath, percent, cancel);
        // The card is written last, so a voice only counts as installed once everything is in place.
        await File.WriteAllTextAsync(voice.CardPath, card, cancel);
        progress.Report(Localizer.Get("Piper_Done"));
    }

    public static void RemoveVoice(PiperVoice voice) {
        if (_processVoice == voice.Key) {
            Shutdown();
        }
        if (Directory.Exists(voice.Folder)) {
            Directory.Delete(voice.Folder, true);
        }
    }

    /// <summary>Removes the engine and its Python, and the downloaded voices too when asked.</summary>
    public static void Uninstall(bool removeVoices) {
        Shutdown();
        if (removeVoices) {
            if (Directory.Exists(Root)) {
                Directory.Delete(Root, true);
            }
            return;
        }
        if (Directory.Exists(PythonFolder)) {
            Directory.Delete(PythonFolder, true);
        }
        File.Delete(BridgePath);
    }

    /// <summary>Speaks the text into a WAV file held in memory. <paramref name="rate"/> is 1 for normal speed.</summary>
    public static async Task<byte[]> SynthesizeAsync(string voiceKey, string text, double rate) {
        PiperVoice voice = Catalog.FirstOrDefault(candidate => candidate.Key == voiceKey && candidate.IsInstalled)
            ?? throw new InvalidOperationException(Localizer.Get("Piper_VoiceMissing"));
        if (!EngineInstalled) {
            throw new InvalidOperationException(Localizer.Get("Piper_NeedsEngine"));
        }
        await Gate.WaitAsync();
        try {
            Process process = await EnsureProcessAsync(voice);
            string request = JsonSerializer.Serialize(new { text, length_scale = 1 / Math.Clamp(rate, 0.4, 3) });
            await process.StandardInput.WriteLineAsync(request);
            await process.StandardInput.FlushAsync();
            string? answer = await process.StandardOutput.ReadLineAsync();
            if ((answer is null) || !answer.StartsWith("OK ", StringComparison.Ordinal)) {
                string problem = answer?.StartsWith("ERR ", StringComparison.Ordinal) == true ? answer[4..] : _lastError;
                Shutdown();
                throw new InvalidOperationException(problem);
            }
            return Convert.FromBase64String(answer[3..]);
        }
        finally {
            Gate.Release();
        }
    }

    /// <summary>Stops the background Piper process, if one is running.</summary>
    public static void Shutdown() {
        Process? process = _process;
        _process = null;
        _processVoice = null;
        if (process is null) {
            return;
        }
        try {
            if (!process.HasExited) {
                process.Kill(true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) {
            // It had already gone.
        }
        process.Dispose();
    }

    /// <summary>Starts the bridge for a voice, or reuses the one already running. Loading a voice takes a few seconds.</summary>
    private static async Task<Process> EnsureProcessAsync(PiperVoice voice) {
        if ((_process is { HasExited: false }) && (_processVoice == voice.Key)) {
            return _process;
        }
        Shutdown();
        await File.WriteAllTextAsync(BridgePath, Bridge);
        Process process = Start($"-X utf8 -u \"{BridgePath}\" \"{voice.ModelPath}\"", true);
        process.ErrorDataReceived += static (_, e) => {
            if (!string.IsNullOrWhiteSpace(e.Data)) {
                _lastError = e.Data;
            }
        };
        process.BeginErrorReadLine();
        string? ready = await process.StandardOutput.ReadLineAsync();
        if (ready != "READY") {
            string problem = _lastError;
            process.Kill(true);
            process.Dispose();
            throw new InvalidOperationException(problem);
        }
        _process = process;
        _processVoice = voice.Key;
        return process;
    }

    private static Process Start(string arguments, bool interactive) {
        ProcessStartInfo info = new(PythonProgram, arguments) {
            WorkingDirectory = Root,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = interactive,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        if (interactive) {
            info.StandardInputEncoding = new UTF8Encoding(false);
        }
        return Process.Start(info) ?? throw new InvalidOperationException(PythonProgram);
    }

    private static async Task RunPythonAsync(string arguments, CancellationToken cancel) {
        using Process process = Start(arguments, false);
        Task<string> output = process.StandardOutput.ReadToEndAsync(cancel);
        Task<string> errors = process.StandardError.ReadToEndAsync(cancel);
        try {
            await process.WaitForExitAsync(cancel);
        }
        catch (OperationCanceledException) {
            process.Kill(true);
            throw;
        }
        if (process.ExitCode != 0) {
            string text = (await errors).Trim();
            throw new InvalidOperationException((text.Length > 0) ? text[Math.Max(0, text.Length - 600)..] : (await output).Trim());
        }
    }

    private static async Task DownloadAsync(string address, string path, IProgress<int>? percent, CancellationToken cancel) {
        string partial = path + ".part";
        using (HttpResponseMessage response = await Client.GetAsync(address, HttpCompletionOption.ResponseHeadersRead, cancel)) {
            response.EnsureSuccessStatusCode();
            long? total = response.Content.Headers.ContentLength;
            await using Stream source = await response.Content.ReadAsStreamAsync(cancel);
            await using FileStream target = File.Create(partial);
            byte[] buffer = new byte[1 << 16];
            long done = 0;
            int last = -1;
            int read;
            while ((read = await source.ReadAsync(buffer, cancel)) > 0) {
                await target.WriteAsync(buffer.AsMemory(0, read), cancel);
                done += read;
                int now = (total > 0) ? (int)(done * 100 / total.Value) : 0;
                if (now != last) {
                    last = now;
                    percent?.Report(now);
                }
            }
        }
        File.Move(partial, path, true);
    }
}