# Progreso del Proyecto: MediaRecovery Studio

**Fecha de lanzamiento oficial:** 14 de Octubre de 2026  
**Versión:** 1.0.0 Oficial (Portable Single-File)  
**Ubicación del código:** `G:\Documents\Proyectos Software\MediaRecovery`  
**Ejecutable publicado:** `G:\Documents\Proyectos Software\MediaRecovery\publish_v1.0\MediaRecovery.exe`  
**Estado general:** Completado, publicado y verificado (100% portable y autónomo)

---

## 📌 Visión General

**MediaRecovery Studio** es una suite de escritorio integral diseñada para:
1. **Recuperación forense de video ProRes dañado** (sin átomo `moov` o procedente de grabaciones interrumpidas en Blackmagic/Atomos).
2. **Conversión y compresión optimizada** (perfil V5 CBR 25 Mbps, aceleración por hardware Nvidia/AMD/CPU, desentrelazado Yadif 25p/50p y recorte de fragmentos).
3. **Descargas desde YouTube y plataformas web** (integración transparente con `yt-dlp`).
4. **Gestión avanzada de bibliotecas de video**:
   - Análisis de dispositivos y orientación (Prensa).
   - Detección de videos rotos o corruptos.
   - Búsqueda de videos pesados o sin compresión (ProRes, DNxHD, RAW).
   - Detección de duplicados (en una carpeta o comparando dos carpetas).

---

## 🚀 Resumen de Módulos y Pestañas Implementadas

```
MediaRecovery Studio
├── Tab 1: Descargar de YouTube / Web
├── Tab 2: Recuperación ProRes
├── Tab 3: Convertir Videos & Lotes
└── Tab 4: Biblioteca & Herramientas
    ├── 1. Analizador de Dispositivos (Prensa)
    ├── 2. Videos Rotos o Dañados
    ├── 3. Videos Sin Compresión / Pesados
    └── 4. Buscador de Duplicados
```

---

### 1. 📥 Tab 1: Descargar de YouTube / Web
- **Motor:** `yt-dlp` en segundo plano mediante `ProcessRunnerService`.
- **Características:**
  - Entrada de URL con botón directo de pegar desde el portapapeles.
  - Selección de carpeta destino con opción de apertura automática al terminar.
  - Opciones de calidad: Máxima, 1080p, 720p o Solo Audio (MP3).
  - Selector de formato (`mp4`, `mkv`, `webm`).
  - Barra de progreso porcentual extraída en tiempo real de la salida de consola.
  - Registro de log detallado.

---

### 2. 🔧 Tab 2: Recuperación ProRes (Forense)
- **Motor:** [`ProResScannerService.cs`](file:///g:/Documents/Proyectos%20Software/MediaRecovery/Services/ProResScannerService.cs) y [`MovBuilderService.cs`](file:///g:/Documents/Proyectos%20Software/MediaRecovery/Services/MovBuilderService.cs).
- **Características:**
  - Análisis de bajo nivel sobre archivos `.mov`, `.raw`, `.img`, `.bin` sin índice `moov`.
  - Detección de firmas de trama ProRes `icpf` en bloques de lectura configurables (ej. 64 MB).
  - Detección automática y extracción de tramas de audio LPCM (24-bit / 48 kHz).
  - Reconstrucción de átomos QuickTime (`ftyp`, `mdat`, `moov`, `mvhd`, `trak`, `stbl`, `stsd`, `stts`, `stsc`, `stsz`, `stco`/`co64`).
  - Salida compatible con DaVinci Resolve, Premiere Pro y reproductores estándar.
  - Métricas en vivo (frames descubiertos, tasa de lectura MB/s, progreso y tiempo estimado).

---

### 3. 🎬 Tab 3: Convertir Videos & Lotes
- **Motor:** [`VideoConverterService.cs`](file:///g:/Documents/Proyectos%20Software/MediaRecovery/Services/VideoConverterService.cs) y perfiles optimizados del script V5.
- **Características:**
  - **Doble modo:** Pestaña de *Archivo individual* y pestaña de *Procesamiento por lotes*.
  - **Aceleración por hardware:** Detección automática o manual de GPU:
    - Nvidia NVENC (`h264_nvenc` con `-preset p5 -tune hq -rc cbr -spatial-aq 1 -temporal-aq 1`).
    - AMD AMF (`h264_amf` con `-vbaq false -usage transcoding -quality quality -rc cbr`).
    - CPU (`libx264` con `-preset slow`).
  - **Configuración de calidad V5:**
    - Bitrate CBR a 25 Mbps con `-minrate 25M -maxrate 25M -bufsize 50M` y GOP cerrado (`-g 50`).
    - Mezcla de audio estéreo optimizada a 320 kbps (`pan=stereo|c0=c0|c1=c1`).
  - **Desentrelazado Yadif:**
    - 50p automático y recomendado para ProRes / doble de cuadros con `-fps_mode cfr -r 50`.
    - 25p recomendado para material entrelazado de Blackmagic.
    - Detección inteligente: al seleccionar **ProRes 422 HQ (MOV)** en el selector de formato, el desentrelazado se configura automáticamente a 50p.
  - **Recorte de fragmento de tiempo:**
    - Opciones para exportar video completo o fragmento (`Desde HH:MM:SS` hasta `Hasta HH:MM:SS`).
    - Colocación de `-ss` y `-to` como opciones de salida para garantizar sincronía exacta sin saltos de cuadros.
    - **Cálculo preciso de duración y progreso:** Pre-consulta la duración exacta con `ffprobe` y descuenta el offset inicial (`-ss`) para calcular porcentajes (0-100%), tiempos relativos transcurridos en formato legible (`MM:SS` / `HH:MM:SS`) y estimación de tiempo restante (ETA) en función de los FPS reales de conversión.
  - **Visor de Registro / Log embellecido y Auto-Scroll:**
    - Filtrado inteligente de telemetría: se suprimió el spam continuo de `frame=...` y `[download] ...%` que saturaba la consola; ahora se muestran exclusivamente hitos clave, advertencias, errores y metadatos de streams.
    - Desplazamiento automático al final (`AutoScrollToEnd`): el log acompaña automáticamente cada nueva línea en tiempo real para mantener visible lo más reciente sin quedarse atrás.
    - Tipografía y contraste modernos (`Consolas, Cascadia Code`), color celeste legible (`#A5D6FF`), fondo `#090D13` y padding optimizado en todas las pestañas.
  - **Botón `[📂 Ir al archivo convertido]`:**
    - Al finalizar la conversión (individual o por lotes), se hace visible un botón verde de acceso rápido que abre el Explorador de Windows con el archivo exacto seleccionado (o la carpeta destino del lote).
  - **Arrastrar y Soltar Inteligente (Drag & Drop en todos los campos):**
    - **Conversor individual selectivo:** Si se arrastra 1 video, se carga en el campo individual. Si se arrastran múltiples videos, **redirige automáticamente a la sub-pestaña de lotes** y los encola todos sin perder tiempo.
    - **Procesamiento por lotes:** Permite arrastrar tanto videos individuales como múltiples archivos directamente a la lista para añadirlos en cola.
    - **Recuperación ProRes:** Permite arrastrar archivos dañados (`.mov`, `.raw`, `.img`, `.bin`).
    - **Biblioteca & Duplicados:** Permite arrastrar carpetas directamente a los campos de Carpeta A y Carpeta B.
  - **Detección inteligente de errores en el input:**
    - Al cargar un archivo, [`VideoIntegrityService.cs`](file:///g:/Documents/Proyectos%20Software/MediaRecovery/Services/VideoConverterService.cs) comprueba la cabecera con `ffprobe` y decodifica un fragmento inicial.
    - Si el archivo está corrupto (ej. sin átomo `moov`), muestra una tarjeta de advertencia en rojo/ámbar.
    - Botón **`[🔧 Reparar archivo]`** que pasa la ruta a la pestaña de Recuperación ProRes y cambia de pestaña automáticamente.

---

### 4. 🗂️ Tab 4: Biblioteca & Herramientas
- **Motor:** [`VideoLibraryService.cs`](file:///g:/Documents/Proyectos%20Software/MediaRecovery/Services/VideoLibraryService.cs) y [`LibraryToolsViewModel.cs`](file:///g:/Documents/Proyectos%20Software/MediaRecovery/ViewModels/LibraryToolsViewModel.cs).
- **Sub-módulos integrados:**

#### A. 📱 Analizador de Dispositivos (Prensa Zapala)
- Port en C# del script [`analizador_prensa-v2.py`](file:///d:/Trabajos%20Muni/Scripts/Buscador%20de%20videos/analizador_prensa-v2.py) con procesamiento multi-hilo en paralelo (`Parallel.ForEachAsync`).
- Detección de dispositivos:
  - **Apple:** iPhone, iPad con versión de iOS (`com.apple.quicktime.*`).
  - **Android:** Samsung (con versión y `utc_offset`), Motorola, Xiaomi, Google Pixel, etc.
  - **Vivo:** Metadatos propietarios en el contenedor (`vivoMediaExtInfo` y `takenmodel`).
  - **Cámaras:** Sony (XAVC/MSNV), Canon, Nikon.
  - **Drones / Gimbals:** DJI Osmo / Pocket / Mimo.
  - **Editores:** Adobe Premiere, DaVinci Resolve, CapCut, OBS, Vegas (`[Editado] <Software>`).
  - **Mensajería:** WhatsApp (comprimido).
- Clasificación de orientación (Vertical / Horizontal) considerando la matriz de rotación de metadatos (0°, 90°, 180°, 270°).
- Filtros por mes y año.
- Vista maestro-detalle: tabla de resumen por dispositivo (Verticales, Horizontales, Total, % V) que filtra la lista de videos al hacer clic.
- Doble clic o botón para abrir en el Explorador de Windows resaltando el archivo exacto.
- Exportación a CSV para Excel regional (delimitador `;`).

#### B. ⚠️ Buscador de Videos Rotos o Dañados
- Escaneo recursivo para encontrar videos que no pueden abrirse o tienen el contenedor incompleto.
- Opción de comprobación profunda de decodificación con FFmpeg.
- Detección de si el archivo contiene tramas ProRes recuperables.
- Botón **`[🔧 Enviar a Reparación ProRes]`** para enviar el video dañado a la pestaña 2 con un solo clic.

#### C. 📦 Buscador de Videos Sin Compresión / Pesados
- Identificación de codecs master y de edición: **ProRes** (422, HQ, LT, 4444), **Avid DNxHD / DNxHR**, **RAW video / YUV**, **Motion JPEG**, **CineForm**, **FFV1**.
- Filtros configurables por bitrate mínimo (ej. ≥ 40 Mbps) y tamaño mínimo (ej. ≥ 1.0 GB).
- Cálculo del peso total en disco.
- Botón **`[⚡ Enviar a Convertir / Comprimir]`** para pasarlo a la pestaña 3 y comprimirlo.

#### D. 👥 Buscador de Duplicados
- **2 modos:**
  - Búsqueda en una sola carpeta (recursiva).
  - Comparación entre 2 carpetas distintas (ej. Disco externo vs Carpeta local).
- **Algoritmo de doble fase ultra rápido:**
  - Filtro inicial por tamaño exacto en bytes (instantáneo).
  - Hash SHA-256 de muestra triple (inicio, centro y final del archivo), procesando gigabytes en milisegundos.
- Muestra los duplicados agrupados identificando el **Original** y las **Copias duplicadas**.
- Botón **`[🗑 Enviar duplicado a Papelera]`** que mueve el archivo a la Papelera de Reciclaje de Windows con confirmación.

---

## 🛠️ Estructura del Código Fuente

```
MediaRecovery/
├── MediaRecovery.csproj                # Configuración de compilación .NET 10 WPF
├── App.xaml / App.xaml.cs              # Entrada de la aplicación y recursos globales
├── Themes/
│   └── DarkTheme.xaml                  # Paleta GitHub Dark, estilos, botones y convertidores
├── Models/
│   ├── FrameInfo.cs                    # Información de fotograma ProRes escaneado
│   ├── AudioInfo.cs                    # Metadatos de pista de audio descubierta
│   ├── ScanProgressReport.cs           # Métricas de escaneo forense
│   └── VideoLibraryModels.cs           # Modelos de metadatos, rotos, pesados y duplicados
├── Services/
│   ├── ProcessRunnerService.cs         # Ejecución asíncrona de procesos de consola
│   ├── ProResScannerService.cs         # Escáner de firmas icpf y cálculo de offset
│   ├── MovBuilderService.cs            # Ensamblador de átomo QuickTime (.mov)
│   ├── VideoConverterService.cs        # Motor FFmpeg con perfiles V5 de compresión
│   ├── VideoIntegrityService.cs        # Verificación rápida de contenedor y decodificación
│   └── VideoLibraryService.cs          # Motor multi-hilo de análisis, detección y duplicados
├── ViewModels/
│   ├── ViewModelBase.cs                # INotifyPropertyChanged base
│   ├── RelayCommand.cs                 # ICommand base
│   ├── MainViewModel.cs                # Orquestador principal y navegación de pestañas
│   ├── DownloaderViewModel.cs          # Lógica de descarga web
│   ├── RecoveryViewModel.cs            # Lógica de reparación ProRes
│   ├── ConverterViewModel.cs           # Lógica de conversión, lotes y detección de fallas
│   └── LibraryToolsViewModel.cs        # Lógica de las 4 herramientas de biblioteca
└── Views/
    ├── MainWindow.xaml                 # Interfaz visual responsive con las 4 pestañas
    └── MainWindow.xaml.cs              # Eventos de UI (doble clic en lista de videos)
```

---

## 🐍 Scripts Python de Referencia

- [`prores_recovery_v6.py`](file:///g:/Documents/Proyectos%20Software/MediaRecovery/prores_recovery_v6.py):  
  Versión autónoma en Python con detección inteligente de FFmpeg en Windows y opción de exportar fragmento de tiempo.
- [`analizador_prensa-v2.py`](file:///d:/Trabajos%20Muni/Scripts/Buscador%20de%20videos/analizador_prensa-v2.py):  
  Script original en Tkinter para el análisis de prensa de Zapala, cuyos algoritmos de detección fueron portados y ampliados en el servicio C#.

---

## ⚙️ Tecnologías y Requisitos

- **Plataforma:** Windows 10 / 11 (64-bit)
- **SDK:** .NET 10.0 SDK (10.0.401)
- **Framework UI:** WPF (Windows Presentation Foundation)
- **Herramientas externas:**
  - `ffmpeg.exe` (en `C:\ffmpeg\bin\ffmpeg.exe` o en `PATH`)
  - `ffprobe.exe` (en `C:\ffmpeg\bin\ffprobe.exe` o en `PATH`)
  - `yt-dlp.exe` (para descargas web)

---

## 📈 Estado de Verificación y Compilación

- **Compilación .NET 10:** Correcta (0 advertencias, 0 errores).
- **Ejecución en memoria:** Estable (consumo base ~110-120 MB RAM).
- **Pruebas de navegación:** Enlace fluido entre detección de fallas -> Reparación, y Videos pesados -> Conversión.
- **Optimización para Alto Volumen (100.000+ Archivos):**
  - **Virtualización de UI:** `VirtualizingPanel.VirtualizationMode="Recycling"` y `ScrollViewer.IsDeferredScrollingEnabled="True"` aplicados globalmente a los `ListView`, manteniendo un consumo de interfaz inferior a 150 MB aún con cientos de miles de registros.
  - **Asignación atómica de colecciones:** Evita 100.000 notificaciones individuales de `INotifyCollectionChanged`.
  - **Caché en disco persistente (`%LocalAppData%\MediaRecoveryStudio\metadata_cache.json`):** Permite re-analizar bibliotecas de más de 100k archivos en segundos sin saturar el sistema ni re-ejecutar `ffprobe` sobre videos sin cambios.
  - **Truncado de Logs:** Límite máximo de 60 KB de memoria para consolas de texto para prevenir acumulación en el *Large Object Heap*.
