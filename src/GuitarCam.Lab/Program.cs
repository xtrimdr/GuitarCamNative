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
                "exportar" => DspCommands.Export(a),
                "click" => DspCommands.Click(a),
                "comprobar-dsp" => DspCommands.Check(a),
                "espectro" => DspCommands.Spectrum(a),
                "lufs" => DspCommands.Lufs(a),
                "masterizar" => DspCommands.Master(a),
                "afinar" => DspCommands.Tune(a),
                "transponer" => TransposeCommands.Run(a),
                "looks" => VideoCommands.Looks(a),
                "camara" => CameraCommands.Run(a),
                "sistema" => SystemCommands.Run(a),
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
                        [--simular [--ruido-2 0.0001]] [--entradas 1,2,3-4]
                                           graba sobre el click; con --monitor te escuchás con la saturación;
                                           --simular graba un tono sin la placa (--ruido-2: la entrada 2 con soplido);
                                           --entradas graba una toma por entrada a la vez, como varias pistas
                                           armadas (toma_e1.wav, toma_e3-4.wav…); con --simular verifica cada WAV
              procesar  entrada.wav [--salida x.wav] [--sat valvula|cinta] [--vida 0.3]
                        [--comp] [--umbral -24] [--ratio 4] [--ataque 10] [--release 150] [--knee 6]
                        [--makeup 0] [--limit] [--comp-estilo rasgueo|arpegio|punteo] [--comp-cant 0.5]
                        [--eq ritmica|punteo|acustica|frente] [--reverb cuarto|sala|salon|placa|catedral]
                        [--delay slapback|corchea|punteada|pingpong|ambiente] [--bpm 120]
                                           aplica efectos de pista y buses a un WAV
              mezclar   carpeta-proyecto|entrada.wav [--salida mezcla.wav] [--segundos N]
                                           renderiza la mezcla completa offline (sin la demora del limitador)
              exportar  [carpeta-proyecto] [--salida nombre] [--carpeta destino] [--calidad alta|estandar|borrador]
                        [--reemplazar] [--prueba] [--audio wav|mp3] [--stems]
                        [--destino project|reels|youtube|ig-square|ig-portrait] [--lufs -14] [--cola 8]
                                           exporta el proyecto a MP4 (o la mezcla en WAV / MP3) con mezcla offline y FFmpeg;
                                           --stems deja además un WAV por pista en "<nombre> - stems";
                                           --destino usa el cuadro y el volumen de esa red (Reels/Shorts/TikTok 9:16,
                                           YouTube 16:9, Instagram 1:1 y 4:5, todas a -14 LUFS); --lufs lleva la mezcla
                                           a ese volumen integrado; --cola deja sonar reverb y delay hasta N s después
                                           del último clip (8 por defecto, 0 = corta ahí). Muestra los LUFS del resultado
              click     [--salida clicks.wav] [--bpm 100]
                                           escribe todos los sonidos de click a un WAV para escucharlos
              lufs      archivo.wav|mp3        volumen integrado (EBU R128) del archivo, como el medidor LUFS del master
              masterizar archivo.wav [--salida x_master.wav] [--cuanto 0.35] [--banda-graves|--banda-medios|--banda-agudos 0..1]
                        [--corte 0|20..150] [--barro dB] [--graves dB] [--presencia dB] [--aire dB] [--inclinacion dB]
                        [--cruce-graves 150] [--cruce-agudos 5000] [--cinta 0..1] [--ancho 0..2] [--graves-mono Hz]
                        [--recorte 0..3] [--empuje 0] [--techo -0.3] [--release 80] [--sin-limitador]
                                           el mastering de la ventana sobre un WAV: cuánto comprime cada banda
                                           ("Cuánto", 0 = nada), cuánto baja el limitador y los LUFS de entrada y salida
              afinar    archivo.wav [--en segundos] [--umbral -50]
                                           la nota que oye el afinador en ese momento del archivo; --umbral es
                                           la sensibilidad de la ventana (dB, de -70 = muy sensible a -30)
              transponer archivo.wav [--semitonos 2] [--salida x_+2st.wav]
                                           cambia el tono sin cambiar el largo (de -12 a +12, admite fracciones = cents),
                                           como el Transponer de la pista: vocoder de fase PVDR + remuestreo sinc
              espectro  [entrada.wav] [--hz 1000] [--nivel -6]
                                           el espectro en vivo del EQ (FFT de 8192 después del EQ, como la web);
                                           sin WAV, comprueba un seno contra el AnalyserNode de Web Audio
              looks     [--look id] [--salida look.cube] [--blancos 0] [--negros 0] [--luz 0]
                                           lista los looks de color con cómo cambian unos colores de prueba y comprueba
                                           que queden en rango; con --look y --salida escribe ese look como LUT .cube
                                           (none = sin look), con los ajustes Blancos, Negros y Sensación de luz (-100..100)
              camara    [listar]           lista las cámaras (DirectShow) y sus formatos; * = la que elige la app
              camara grabar [--dispositivo X | --ip 192.168.1.50:4747] [--resolucion native|1080p|720p|480p]
                        [--segundos 5] [--salida camara.mp4]
                                           graba el video como una toma (H.264, 30 fps, keyframe cada 1 s)
              camara verificar-corte [--dispositivo X]
                                           corta una grabación a la mitad (con y sin FFmpeg) y comprueba que el video quede
              sistema   [--segundos 5]     muestra CPU, GPU y RAM cada 1,5 s como la barra de arriba, y si está FFmpeg
              proyecto abrir    carpeta    abre un proyecto nativo y muestra pistas, avisos e historial
              proyecto versiones carpeta   lista las versiones guardadas, de más nueva a más vieja
              proyecto volver   carpeta n  restaura la versión n y conserva el estado actual en el historial
              proyecto verificar-deshacer  verifica fotos, agrupación de sliders, rehacer y color e ícono de pista sin modificar archivos
              proyecto verificar-clips     verifica anclaje, imanes, recortes, fades y operaciones de pistas
              proyecto clips carpeta       lista los IDs de pistas y clips para editar desde la consola
              proyecto encuadre carpeta clip [--zoom 1.5] [--panx 0.2] [--pany -0.3] [--espejar] [--restaurar]
              proyecto sincronia carpeta clip [--audio-ms -30] [--camara-ms 80] [--heredar]
              proyecto formato  carpeta [--relacion 16:9|9:16|1:1] [--disposicion lado-a-lado|apilado]
                                           como la ventana: 9:16 pasa a apilado y 16:9 a lado a lado
              proyecto editar carpeta mover|recortar-inicio|recortar-final|fade-in|fade-out clip delta-segundos
                        [--libre] [--iman 1/4] [--a-clips inicio|final|ambos|no] [--pps 100]   con imán, de a pasos
                        enteros; --a-clips: ese borde se pega al de otro clip a menos de 8 px (a ese zoom), si queda más
                        cerca que el paso de la grilla; sin imán ni --a-clips, o --libre, sin pegarse a nada
              proyecto editar carpeta alinear clip [--iman 1/4]   el comienzo de la toma a la línea del imán más cercana
              proyecto editar carpeta a-pista clip pista [--intercambiar clip] | reordenar desde hasta
              proyecto editar carpeta inicio|fijar-corte|restaurar|quitar-clip clip | quitar-pista pista
              proyecto editar carpeta nueva-pista | agregar-toma medio [--pista pista]
              proyecto editar carpeta borrar-rango pista desde --hasta fin   saca ese tramo de los clips de la pista (queda el hueco)
              proyecto editar carpeta nueva-carpeta [nombre] [--pistas id,id] | quitar-carpeta carpeta   agrupa pistas (quitarla las deja sueltas)
              proyecto editar carpeta a-carpeta pista carpeta|raiz [--en n] | sacar-de-carpeta pista | mover-carpeta carpeta n
              proyecto editar carpeta carpeta id [--nombre X] [--volumen 0.8] [--mute] [--solo] [--plegar]   fader, mute y solo de la carpeta (como un VCA)
              proyecto editar carpeta transponer pista semitonos   cambia el tono de la pista sin cambiar el largo (-12..+12, 0 = original)
                                           y deja sus tomas transpuestas en cache/ (como el Transponer del inspector)
              proyecto editar carpeta guardar-plantilla pista [nombre] | usar-plantilla id|nombre   plantillas de pista (pistas.json)
              proyecto plantillas-de-pista x   lista las plantillas de pista guardadas
              proyecto verificar-carpetas  verifica el orden de carpetas y pistas, mover adentro y afuera, quitar y deshacer
              proyecto marcadores carpeta [agregar segundos [nombre] | mover id segundos | renombrar id nombre | quitar id]
                                           los marcadores del tema (Intro, Solo…); sin operación, los lista
              proyecto repetir carpeta [desde hasta | --quitar]   el rango A–B que repite el loop; sin nada, lo muestra
              proyecto verificar-marcadores verifica orden, nombres, saltos, deshacer y guardado de marcadores y del rango A–B
              proyecto editar carpeta ganancia clip dB | curva clip "seg:dB,..."   volumen del clip y su curva (seg desde el inicio de la toma)
              proyecto verificar-tomas     verifica a qué pista va una toma nueva, su nombre y su canal
              proyecto importar-audio carpeta archivo [--en segundos] [--pista id]   copia un audio de afuera a media/ y lo pone en una pista
              proyecto verificar-importar  verifica la importación de audio (formato, nombres, archivos rotos)
              proyecto historial carpeta   tomas (en uso / no usadas), exportaciones y actividad, como la pestaña Historial
              proyecto historial carpeta quitar|usar medio | borrar-no-usadas | borrar-exportacion n
              proyecto verificar-historial verifica el registro de tomas, la Papelera y las frases de la actividad
              proyecto toma     carpeta [--segundos 3] [--pista id] [--camara]
                                           graba un tono como la ventana (--pista: la pista seleccionada;
                                           --camara: con el video de la cámara que elige la app)
              proyecto cortar   carpeta [--segundos 3] [--camara]
                                           graba un tono (y el video) y corta sin cerrar, para probar la recuperación
              proyectos lista|plantillas [--raiz X]
              proyectos nuevo "Nombre" [--plantilla id] | renombrar carpeta "Nombre" | duplicar carpeta
              proyectos borrar carpeta | guardar-plantilla carpeta "Nombre"
              proyectos previsualizar "Nombre" | olvidar carpeta | carpeta ruta
              proyectos borrar-plantilla id
              proyectos plantillas-video | guardar-plantilla-video carpeta "Nombre"
              proyectos aplicar-plantilla-video carpeta id|"Nombre" | borrar-plantilla-video id
                                           las plantillas de video (filtro, ajustes y efectos) de la pestaña Color
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
