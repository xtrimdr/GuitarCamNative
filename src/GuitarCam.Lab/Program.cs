using System.Globalization;
using System.Text;

namespace GuitarCam.Lab;

static class Program
{
    // ASIO drivers are COM objects: they want an STA thread.
    [STAThread]
    static int Main(string[] argv)
    {
        Console.OutputEncoding = Encoding.UTF8;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        var a = new Args(argv);
        try
        {
            return a.Command switch
            {
                "dispositivos" => AudioCommands.Devices(),
                "panel" => AudioCommands.Panel(a),
                "latencia" => AudioCommands.Latency(a),
                "grabar" => AudioCommands.Record(a),
                "procesar" => DspCommands.Process(a),
                "verificar" => DspCommands.Verify(a),
                "click" => DspCommands.Click(a),
                _ => Help(),
            };
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"Error: {e.Message}");
            return 1;
        }
    }

    static int Help()
    {
        Console.WriteLine("""
            gclab: pruebas del motor de audio de GuitarCam en .NET

              dispositivos                 lista los drivers ASIO y los dispositivos WASAPI
              panel     [--driver X]       abre el panel del driver ASIO (tamaño de buffer)
              latencia  [--driver X] [--entrada 1] [--salida 1] [--sr 48000]
                                           mide la latencia ida y vuelta (cable de una salida a una entrada)
              grabar    [--driver X] [--segundos 15] [--bpm 100] [--cuenta 4] [--click madera]
                        [--volumen-click 0] [--canal 1|2|estereo] [--monitor]
                        [--sat valvula|cinta] [--vida 0.3] [--salida toma.wav]
                                           graba sobre el click; con --monitor te escuchás con la saturación
              procesar  entrada.wav [--salida x.wav] [--sat valvula|cinta] [--vida 0.3]
                        [--comp] [--umbral -24] [--ratio 4] [--ataque 10] [--release 150] [--knee 6]
                        [--makeup 0] [--limit]
                                           aplica saturación y/o compresor a un WAV
              verificar [entrada.wav]      compara el DSP portado con los worklets de la app web (necesita Node)
              click     [--salida clicks.wav] [--bpm 100]
                                           escribe todos los sonidos de click a un WAV para escucharlos
            """);
        return 0;
    }
}

sealed class Args
{
    readonly Dictionary<string, string?> opts = new(StringComparer.OrdinalIgnoreCase);
    public string Command { get; } = "";
    public List<string> Positional { get; } = [];

    public Args(string[] argv)
    {
        for (int i = 0; i < argv.Length; i++)
        {
            string s = argv[i];
            if (s.StartsWith("--"))
            {
                string key = s[2..];
                string? value = i + 1 < argv.Length && !argv[i + 1].StartsWith("--") ? argv[++i] : null;
                opts[key] = value;
            }
            else if (Command == "") Command = s.ToLowerInvariant();
            else Positional.Add(s);
        }
    }

    public bool Flag(string key) => opts.ContainsKey(key);

    public string? Get(string key) => opts.TryGetValue(key, out var v) ? v : null;

    public string Get(string key, string fallback) => Get(key) ?? fallback;

    public double Double(string key, double fallback)
    {
        var v = Get(key);
        if (v == null) return fallback;
        return double.TryParse(v.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var d)
            ? d
            : throw new ArgumentException($"--{key}: \"{v}\" no es un número.");
    }

    public int Int(string key, int fallback) => (int)Math.Round(Double(key, fallback));
}
