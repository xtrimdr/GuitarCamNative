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
                "mezclar" => DspCommands.Mix(a),
                "verificar" => DspCommands.Verify(a),
                "click" => DspCommands.Click(a),
                "proyecto" => ProjectCommands.Run(a),
                "proyectos" => LibraryCommands.Run(a),
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
                        [--makeup 0] [--limit] [--comp-estilo rasgueo|arpegio|punteo] [--comp-cant 0.5]
                        [--eq ritmica|punteo|acustica|frente] [--reverb cuarto|sala|salon|placa|catedral]
                        [--delay slapback|corchea|punteada|pingpong|ambiente] [--bpm 120]
                                           aplica efectos de pista y buses a un WAV
              mezclar   carpeta-proyecto|entrada.wav [--salida mezcla.wav] [--segundos N]
                                           renderiza la mezcla completa offline (sin la demora del limitador)
              verificar [entrada.wav]      compara el DSP portado con los worklets de la app web (necesita Node)
              click     [--salida clicks.wav] [--bpm 100]
                                           escribe todos los sonidos de click a un WAV para escucharlos
              proyecto importar carpeta-web [--destino X]
                                           pasa un proyecto de la app web al formato nativo (no toca el original)
              proyecto abrir    carpeta    abre un proyecto nativo y muestra pistas, avisos e historial
              proyecto versiones carpeta   lista las versiones guardadas, de más nueva a más vieja
              proyecto volver   carpeta n  restaura la versión n y conserva el estado actual en el historial
              proyecto verificar-deshacer  verifica fotos, agrupación de sliders y rehacer sin modificar archivos
              proyecto verificar-clips     verifica anclaje, imanes, recortes, fades y operaciones de pistas
              proyecto clips carpeta       lista los IDs de pistas y clips para editar desde la consola
              proyecto encuadre carpeta clip [--zoom 1.5] [--panx 0.2] [--pany -0.3] [--espejar] [--restaurar]
              proyecto editar carpeta mover|recortar-inicio|recortar-final|fade-in|fade-out clip delta-segundos
                        [--libre] [--iman 1/4] [--pps 100] [--cabezal 0]
              proyecto editar carpeta a-pista clip pista [--intercambiar clip] | reordenar desde hasta
              proyecto editar carpeta inicio|fijar-corte|restaurar|quitar-clip clip | quitar-pista pista
              proyecto editar carpeta nueva-pista | agregar-toma medio [--pista pista]
              proyecto cortar   carpeta [--segundos 3]
                                           graba un tono y corta sin cerrar, para probar la recuperación
              proyectos lista|plantillas [--raiz X]
              proyectos nuevo "Nombre" [--plantilla id] | renombrar carpeta "Nombre" | duplicar carpeta
              proyectos borrar carpeta | guardar-plantilla carpeta "Nombre"
              proyectos previsualizar "Nombre" | olvidar carpeta | carpeta ruta
              proyectos borrar-plantilla id
              proyectos nuevo "Nombre" [--plantilla id] [--carpeta vacía]
                                           el gestor de proyectos sin la ventana (--raiz: otra carpeta de proyectos)
                                           --datos X: configuración, recientes y plantillas aislados para pruebas
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
