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
                "tempo" => TempoChecks.Run(a),
                "extraer-midi" => ToMidiChecks.Run(a),
                "transponer" => TransposeCommands.Run(a),
                "looks" => VideoCommands.Looks(a),
                "leer-video" => VideoCommands.ReadVideo(a),
                "nitidez" => DetailCommands.Run(a),
                "camara" => CameraCommands.Run(a),
                "sistema" => SystemCommands.Run(a),
                "version" => VersionCommands.Run(a),
                "proyecto" => ProjectCommands.Run(a),
                "midi" => MidiCommands.Run(a),
                "vst" => VstCommands.Run(a),
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
                        [--sat valvula|cinta] [--vida 0.3] [--comp] [--comp-estilo rhythm|arpeggio|lead] [--comp-cant 0.5]
                        [--eq rhythm|lead|acoustic|front] [--cadena vida,comp,eq,puerta]
                                           renderiza la mezcla completa offline (sin la demora del limitador); con un WAV,
                                           --cadena arma los slots de la pista en ese orden (se pueden repetir: eq,vida,eq)
              exportar  [carpeta-proyecto] [--salida nombre] [--carpeta destino] [--calidad alta|estandar|borrador]
                        [--reemplazar] [--prueba] [--audio wav|mp3] [--stems]
                        [--destino project|reels|youtube|ig-square|ig-portrait] [--volumen como-esta|youtube|…]
                        [--lufs -14] [--cola 8] [--rango 12-45|1:05-1:30|loop]
                        [--codificador auto|cpu|nvenc|amf|qsv] [--resolucion 720|1080|1440|2160] [--bitrate 20]
                                           exporta el proyecto a MP4 (o la mezcla en WAV / MP3) con mezcla offline y FFmpeg;
                                           --stems deja además un WAV por pista en "<nombre> - stems";
                                           --destino usa el cuadro de esa red (Reels/Shorts/TikTok 9:16, YouTube 16:9,
                                           Instagram 1:1 y 4:5); el volumen es el Destino del master del proyecto
                                           (--volumen lo cambia para esta exportación; --lufs lleva la mezcla a ese
                                           volumen integrado); --cola deja sonar reverb y delay hasta N s después
                                           del último clip (8 por defecto, 0 = corta ahí); --rango exporta solo ese tramo
                                           (loop = el A–B del proyecto), con su cola después de B. Muestra los LUFS del resultado;
                                           --codificador elige quién codifica el video (auto = la placa NVIDIA, AMD o
                                           Intel si hay, si no el procesador); --resolucion y --bitrate (Mbps) son
                                           el "Avanzado" del diálogo: el lado corto del cuadro y el bitrate del video
              click     [--salida clicks.wav] [--bpm 100]
                                           escribe todos los sonidos de click a un WAV para escucharlos
              lufs      archivo.wav|mp3        volumen integrado (EBU R128) del archivo, como el medidor LUFS del master
              masterizar archivo.wav [--salida x_master.wav] [--cuanto 0.35] [--banda-graves|--banda-medios|--banda-agudos 0..1]
                        [--corte 0|20..150] [--barro dB] [--graves dB] [--presencia dB] [--aire dB] [--inclinacion dB]
                        [--cruce-graves 150] [--cruce-agudos 5000] [--cinta 0..1] [--ancho 0..2] [--graves-mono Hz]
                        [--recorte 0..3] [--empuje 0] [--techo -1] [--release 80] [--sin-limitador]
                        [--volumen youtube|instagram|tiktok|spotify|apple] [--pua 0..1]
                        [--look natural|glue|bright|warm|front|pick [--cuanto-look 1]] [--sugerime]
                                           el mastering de la ventana sobre un WAV: cuánto comprime cada banda
                                           ("Cuánto", 0 = nada), cuánto baja el limitador y los LUFS de entrada y salida;
                                           --volumen es el Destino: mide el archivo entero y usa la ganancia que lo lleva
                                           al volumen de esa red en vez del empuje. Con mucha ganancia el soft clip se
                                           lleva solo una parte (más de 2 dB sobre el techo: la mitad, hasta 4 dB);
                                           --pua es la cuarta banda (baja 2-5 kHz cuando pega la púa); --look pone
                                           un look al --cuanto-look (0 = Natural, 1 = el look entero); --sugerime
                                           muestra qué oye en el archivo y qué propone, y masteriza con eso
              afinar    archivo.wav [--en segundos] [--umbral -50]
                                           la nota que oye el afinador en ese momento del archivo; --umbral es
                                           la sensibilidad de la ventana (dB, de -70 = muy sensible a -30)
              tempo     archivo [--desde s] [--hasta s]
                                           los BPM del archivo (o de ese tramo), su primer golpe y qué tan seguro
                                           está, como "Detectar tempo" en un clip (el doble y la mitad también valen)
              extraer-midi archivo [--melodia] [--desde s] [--hasta s] [--inicio 0.5] [--nota 0.3]
                        [--min-ms 128] [--midi notas.mid] [--bpm 120]
                                           las notas del archivo (o de ese tramo), como "Extraer MIDI" en un clip
                                           (Basic Pitch); --melodia deja una nota a la vez; --inicio y --nota son
                                           qué tan seguro tiene que estar de que una nota empieza y sigue sonando
                                           (más bajo = más notas); --midi las guarda en un .mid
              extraer-midi --comprobar     solo las comprobaciones del extractor (las de comprobar-dsp)
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
              leer-video archivo [--ancho 640] [--saltos 20] [--cpu] [--cuadro 12.5 [--salida cuadro.bmp]] [--comparar]
                                           mide el decodificador de la preview (FFmpeg dentro de la app): abrir, leer
                                           seguido, saltar y la memoria por cuadro; --cpu sin la placa de video;
                                           --cuadro guarda ese cuadro; --comparar mide ffmpeg.exe por salto y la
                                           diferencia de imagen con él
              nitidez   [video|imagen] [--en 1] [--nitidez 60] [--claridad 60] [--resplandor 0] [--look hdr|dreamy]
                        [--intensidad 100] [--alto 1080] [--ancho-preview 640] [--carpeta dir]
                                           compara Nitidez, Claridad y Resplandor (y lo que suman los looks HDR y
                                           Soñado) de la exportación (FFmpeg) con los de la preview
                                           sobre el mismo cuadro (de un video o uno de prueba), a tamaño de exportación y
                                           al ancho que muestra la preview (640, menos con zoom); mide cuánto cuesta
                                           por cuadro y deja PNG
              camara    [listar]           lista las cámaras (DirectShow) y sus formatos; * = la que elige la app
              camara grabar [--dispositivo X | --ip 192.168.1.50:4747] [--resolucion native|1080p|720p|480p]
                        [--segundos 5] [--salida camara.mp4]
                                           graba el video como una toma (H.264, 30 fps, keyframe cada 1 s)
              camara verificar-corte [--dispositivo X]
                                           corta una grabación a la mitad (con y sin FFmpeg) y comprueba que el video quede
              sistema   [--segundos 5]     muestra CPU, GPU y RAM cada 1,5 s como la barra de arriba, si está FFmpeg
                                           y con qué placa de video exporta
              version                      esta versión contra la última release de GitHub (el aviso de "Acerca de")
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
              proyecto editar carpeta borrar-rango pista[,pista…] desde --hasta fin [--cerrar]   saca ese tramo de los clips de
                        esas pistas; queda el hueco, o con --cerrar lo de después se corre y lo tapa
              proyecto editar carpeta al-frente clip   lo pone arriba en su pista: donde se pisan, es el que se ve
              proyecto mezcla carpeta [ab|copiar|olvidar]   el A | B del mixer: pasar a la otra mezcla, copiar esta sobre la
                                           otra u olvidarla (sin nada, dice cuál suena)
              proyecto editar carpeta bloquear|desbloquear clip|pista   un clip (o toda la pista) bloqueado no se mueve, no se
                                           recorta ni se borra, y una toma nueva no lo pisa
              proyecto editar carpeta transicion clip corte|fundido|negro [--segundos 0.5]   la transición que entra a ese clip desde
                                           el que termina donde empieza: funde la imagen y el sonido, centrada en la unión
              proyecto transiciones carpeta   lista dónde se tocan los clips de cada pista y qué transición tiene cada unión
              proyecto editar carpeta nueva-carpeta [nombre] [--pistas id,id] | quitar-carpeta carpeta   agrupa pistas (quitarla las deja sueltas)
              proyecto editar carpeta a-carpeta pista carpeta|raiz [--en n] | sacar-de-carpeta pista | mover-carpeta carpeta n
              proyecto editar carpeta carpeta id [--nombre X] [--plegar] [--ocultar]   renombra y pliega una carpeta (--ocultar: un
                                           separador se oculta o se muestra en el timeline)
              proyecto editar carpeta nuevo-separador [nombre] [--en n] [--visible]   un separador con nombre en ese lugar de la secuencia (oculto en el timeline salvo --visible)
              proyecto editar carpeta lado id izquierda|derecha   de qué lado del mixer va la tira (pista, carpeta, separador o bus)
              proyecto editar carpeta transponer pista semitonos   cambia el tono de la pista sin cambiar el largo (-12..+12, 0 = original)
                                           y deja sus tomas transpuestas en cache/ (como el Transponer del inspector)
              proyecto editar carpeta guardar-plantilla pista [nombre] | usar-plantilla id|nombre | aplicar-plantilla pista nombre   plantillas de pista (pistas.json)
              proyecto plantillas-de-pista x   lista las plantillas de pista guardadas
              proyecto verificar-carpetas  verifica el orden de carpetas y pistas, mover adentro y afuera, quitar y deshacer
              proyecto marcadores carpeta [agregar segundos [nombre] | mover id segundos | renombrar id nombre | quitar id | capitulos]
                                           los marcadores del tema (Intro, Solo…); sin operación, los lista
              proyecto repetir carpeta [desde hasta | --quitar]   el rango A–B que repite el loop; sin nada, lo muestra
              proyecto duracion carpeta           cuánto dura el tema y si entra en cada red (historias, X, Reels, TikTok)
              proyecto verificar-marcadores verifica orden, nombres, saltos, deshacer y guardado de marcadores y del rango A–B
              proyecto verificar-paquete   empaqueta un proyecto con una toma de afuera y lo desempaqueta: lo que suena, el respaldo y un ZIP tramposo
              proyecto verificar-bloqueo   la regla del bloqueo: un clip o una pista bloqueados no se mueven, recortan ni borran, y grabar no los pisa
              proyecto verificar-ab        el A/B de la mezcla: ida y vuelta, mute y solo quietos, pistas y buses nuevos, deshacer
              proyecto editar carpeta ganancia clip dB | curva clip "seg:dB,..."   volumen del clip y su curva (seg desde el inicio de la toma)
              proyecto verificar-tomas     verifica a qué pista va una toma nueva, su nombre y su canal
              proyecto importar-audio carpeta archivo [--en segundos] [--pista id]   copia un audio de afuera a media/ y lo pone en una pista
              proyecto verificar-importar  verifica la importación de audio (formato, nombres, archivos rotos)
              proyecto insertos carpeta pista [agregar vida|comp|eq|puerta [--en n] | quitar n | mover n m | duplicar n | prender n | apagar n | plegar n]
                                           los slots de efectos de la pista (número o id), en el orden en que suenan; sin
                                           operación, los lista. Como el menú de cada efecto en la tira ancha del mixer;
                                           plegar n lo pliega o despliega en el mixer (no cambia el sonido)
              proyecto insertos carpeta --bus id|n [agregar reverb|delay|vida|comp|eq|puerta … | quitar n | …]
                                           lo mismo para un bus (la reverb y el delay solo van en buses)
              proyecto buses carpeta [agregar [nombre] [--tipo reverb|delay|vacio] | quitar n | renombrar n nombre |
                        volumen n 0.8 | paneo n -0.3 | mute n]
                                           los buses, en el orden en que suenan, con sus slots, su salida y sus envíos;
                                           uno nuevo trae una reverb, un delay o nada; quitar uno se lleva los envíos que
                                           le llegaban y lo que salía a él va al master
              proyecto envios carpeta quien [bus [nivel] [--pre|--post] [--apagar|--prender] [--quitar|--sumar]] [--salida bus|master]
                                           la salida y los envíos de una pista (número o id), carpeta:n o bus:n; --pre lo
                                           saca antes del fader (una carpeta no tiene). Un bus sale solo al master: con
                                           bus:n lo rechaza. --quitar saca el bus de su lista (apagado, con su nivel) y
                                           --sumar lo vuelve a poner. Las opciones van después del nivel
              proyecto historial carpeta   tomas (en uso / no usadas), exportaciones y actividad, como la pestaña Historial
              proyecto historial carpeta quitar|usar medio | borrar-no-usadas | borrar-exportacion n
              proyecto verificar-historial verifica el registro de tomas, la Papelera y las frases de la actividad
              proyecto toma     carpeta [--segundos 3] [--pista id] [--camara]
                                           graba un tono como la ventana (--pista: la pista seleccionada;
                                           --camara: con el video de la cámara que elige la app)
              proyecto cortar   carpeta [--segundos 3] [--camara]
                                           graba un tono (y el video) y corta sin cerrar, para probar la recuperación
              midi ver carpeta             las pistas MIDI con sus clips y sus notas
              midi pista carpeta           agrega una pista MIDI (suena con un tono de prueba hasta que tenga instrumento)
              midi clip carpeta [--pista n|id] [--en segundos] [--compases 4]   un clip MIDI vacío en esa pista
              midi nota carpeta --clip id [--altura C4|60] [--pulso 0] [--largo 1] [--vel 100]
                                           una nota en el clip (pulso y largo en tiempos desde su comienzo)
              midi acordes carpeta --clip id   reemplaza sus notas por Do, Sol, Lam, Fa (un compás cada uno)
              midi quitar carpeta --clip id    saca el clip MIDI
              midi cuantizar carpeta --clip id [--grilla 1/4|1/8|1/16|1/32|1/8T]
                                           mueve el comienzo de sus notas a la grilla de la canción (el largo queda)
              midi velocidad carpeta --clip id [--rango 100]
                                           escala las velocidades alrededor de su promedio (50 las acerca, 0 las
                                           empareja, 150 les da más dinámica)
              midi humanizar carpeta --clip id [--cantidad 10] [--semilla n]
                                           mueve cada velocidad al azar hasta ± cantidad
              midi entradas                los teclados y controladores MIDI que ve Windows, con sus ajustes de
                                           Configuración → MIDI (también los que están desconectados)
              midi escuchar [--entrada nombre] [--segundos 10]
                                           abre las entradas como la ventana e imprime lo que llega, y lo que toma
                                           el motor (después del canal y la curva de velocidad)
              midi instrumento carpeta [--pista n] --plugin "nombre" | --quitar
                                           el instrumento VST3 que toca la pista (de vst escanear); --quitar vuelve al
                                           tono de prueba. gclab mezclar lo renderiza con el plugin
              midi comprobar               verifica el sonido de las notas (bloques, release, exportación, tempo) y el
                                           teclado (suena como la nota escrita, pedal, tomas, cuantizar, velocidad)
                                           (gclab mezclar carpeta renderiza el proyecto con sus pistas MIDI)
              vst escanear [--todo] [--carpeta X] [--archivo X]
                                           busca los plugins VST3 en las carpetas de vst carpetas (los guarda en
                                           plugins.json; --todo los abre todos de nuevo, --archivo reintenta uno solo)
              vst lista                    los plugins encontrados, instrumentos y efectos (y cuáles están ocultos)
              vst carpetas [--agregar X] [--sacar X]
                                           dónde busca: las de Windows y las que sumaste (como Configuración → Plugins)
              vst ocultar|mostrar "nombre" lo saca de las listas de la app o lo vuelve a poner
              vst probar "nombre" [--segundos 4] [--salida x.wav] [--ventana]
                                           toca un acorde con ese plugin a un WAV, como la exportación (--ventana abre
                                           antes su ventana y usa lo que dejes)
              proyectos lista|plantillas [--raiz X]
              proyectos nuevo "Nombre" [--plantilla id] | renombrar carpeta "Nombre" | duplicar carpeta
              proyectos borrar carpeta | guardar-plantilla carpeta "Nombre"
              proyectos empaquetar carpeta [--completo] [--salida x.zip] | desempaquetar x.zip
                                           el proyecto en un ZIP (solo lo que suena, o con --completo todas las tomas,
                                           versiones y exportaciones) y de vuelta a un proyecto nuevo
              proyectos previsualizar "Nombre" | olvidar carpeta | carpeta ruta
              proyectos buscar carpeta     qué abre "Abrir carpeta" con esa carpeta (el proyecto, uno adentro, vacía, de la web…)
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
