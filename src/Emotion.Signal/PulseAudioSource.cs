using System.Diagnostics;

namespace Emotion.Signal;

/// <summary>
/// Le vrai son, capte par PulseAudio.
///
/// Le meme adaptateur sert aux deux situations, seul le nom du peripherique change :
///
///   <c>...analog-stereo.monitor</c>  ce qui sort des haut-parleurs, pour essayer sans
///                                    materiel : on lance un morceau et le visuel suit.
///   <c>alsa_input...analog-stereo</c> l'entree ligne, le jour ou la table est branchee.
///
/// On passe par <c>parec</c> en sous-processus plutot que par une liaison native : le
/// binaire est present sur toute machine PulseAudio ou PipeWire, il rend du PCM brut sur
/// sa sortie standard, et cela evite une dependance native a compiler par plateforme.
/// Le cout est un processus fils, et deux pieges qu'il faut traiter — voir plus bas.
/// </summary>
public sealed class PulseAudioSource : IAudioSource, ILearnsTracks, IAcceptsCue, IMaitre
{
    private const int SampleRate = 48_000;

    private readonly string? _device;
    private readonly SpectrumAnalyzer _analyzer;

    public SpectrumAnalyzer Analyzer => _analyzer;
    private readonly Action<string>? _log;

    /// <param name="device">
    /// Nom du peripherique PulseAudio. Nul signifie la source par defaut.
    /// <c>pactl list short sources</c> les enumere.
    /// </param>
    /// <param name="log">Journal facultatif, pour voir passer les relances.</param>
    /// <param name="separate">
    /// Separer le percussif de l'harmonique avant analyse. Coute 64 ms de latence : on
    /// doit pouvoir couper pour comparer avec et sans sur le meme morceau.
    /// </param>
    public PulseAudioSource(string? device = null, Action<string>? log = null, bool separate = false)
    {
        _device = string.IsNullOrWhiteSpace(device) ? null : device;
        _analyzer = new SpectrumAnalyzer(SampleRate, separate);
        _log = log;
    }

    public string Name =>
        (_device is null ? "entree par defaut" : _device)
        + (_analyzer.Separating ? " · HPSS" : "")
        + $" · retard {_analyzer.LatencyMs:0} ms";

    /// <summary>Passage de relais : reprend le tempo trouve par un autre analyseur.</summary>
    public void AdoptTempo(float bpm, long tMs) => _analyzer.AdoptTempo(bpm, tMs);

    public void NewTrack() => _analyzer.NewTrack();

    /// <summary>Ou en est le fondu. Pendant, les voies suivent sans former leur portrait.</summary>
    public float Fondu { set => _analyzer.Fondu = value; }

    /// <summary>Reprend ce qu'on savait de ce disque : portraits des sources et tempo.</summary>
    public void Resume(in TrackKnowledge knowledge) => _analyzer.Reprendre(knowledge);

    /// <summary>Rend ce qu'on sait maintenant, pour rangement.</summary>
    public TrackKnowledge Park(string id, in TrackKnowledge previous) =>
        _analyzer.Connaissance(id, previous);

    /// <summary>
    /// Lit sans fin. Si <c>parec</c> s'arrete — peripherique debranche, serveur audio
    /// redemarre, carte son qui disparait — on le relance au lieu de rendre la main :
    /// un set ne doit pas mourir parce qu'un cable a bouge.
    /// </summary>
    public async IAsyncEnumerable<VisualFrame> ReadAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var start = DateTime.UtcNow;

        while (!ct.IsCancellationRequested)
        {
            await foreach (var frame in ReadOnceAsync(start, ct))
                yield return frame;

            if (ct.IsCancellationRequested) yield break;

            _log?.Invoke("parec s'est arrete, relance dans une seconde");
            try { await Task.Delay(TimeSpan.FromSeconds(1), ct); }
            catch (OperationCanceledException) { yield break; }
        }
    }

    /// <summary>Une session de capture, du lancement de parec a son arret.</summary>
    private async IAsyncEnumerable<VisualFrame> ReadOnceAsync(
        DateTime start,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        // Mono : le visuel n'a que faire de la stereo, et cela divise par deux le
        // volume de donnees a traiter cinquante fois par seconde.
        var args = $"--format=s16le --rate={SampleRate} --channels=1 --latency-msec=20";
        if (_device is not null) args += $" --device={_device}";

        using var proc = new Process
        {
            StartInfo = new ProcessStartInfo("parec", args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            }
        };

        if (!proc.Start())
            throw new InvalidOperationException("impossible de lancer parec");

        // Le piege des sous-processus : une sortie d'erreur redirigee et jamais lue
        // remplit le tampon du tube, et parec se bloque alors en ecriture — donc cesse
        // aussi d'alimenter sa sortie standard. Le flux s'arrete sans la moindre erreur.
        // On draine donc stderr en continu.
        _ = DrainAsync(proc.StandardError, ct);

        try
        {
            var stream = proc.StandardOutput.BaseStream;
            var bytes = new byte[SpectrumAnalyzer.Window * 2];   // s16le
            var window = new float[SpectrumAnalyzer.Window];

            // OU PASSENT LES DEUX SECONDES ? La boucle du serveur a montre que le trou
            // est entierement dans l'attente de l'image suivante — ni le bus, ni la
            // diffusion, ni le ramasse-miettes. Or cette attente couvre deux choses tres
            // differentes : lire une fenetre dans le tube de parec, et l'analyser. Les
            // separer est la seule facon de ne pas optimiser au hasard.
            var chrono = System.Diagnostics.Stopwatch.StartNew();

            while (!ct.IsCancellationRequested)
            {
                var t0 = chrono.Elapsed.TotalMilliseconds;

                // Une fenetre entiere ou rien : une fenetre partielle produirait un
                // spectre faux, avec des attaques inventees a chaque bord.
                if (!await FillAsync(stream, bytes, ct)) yield break;

                var t1 = chrono.Elapsed.TotalMilliseconds;

                for (var i = 0; i < window.Length; i++)
                {
                    var v = (short)(bytes[i * 2] | (bytes[i * 2 + 1] << 8));
                    window[i] = v / 32768f;
                }

                var t = (long)(DateTime.UtcNow - start).TotalMilliseconds;
                var image = _analyzer.Analyze(window, t);
                var t2 = chrono.Elapsed.TotalMilliseconds;

                // Un pas d'analyse vaut 21 ms. Au-dela de quatre, ce n'est plus une
                // hesitation du planificateur.
                if (t2 - t0 > 85)
                    _log?.Invoke($"capture lente a {t / 1000f:F1} s : {t2 - t0:F0} ms " +
                                 $"— lecture du tube {t1 - t0:F0} · analyse {t2 - t1:F0}");

                yield return image;
            }
        }
        finally
        {
            try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { }
        }
    }

    /// <summary>Vide la sortie d'erreur pour qu'elle ne bloque jamais le processus.</summary>
    private async Task DrainAsync(StreamReader err, CancellationToken ct)
    {
        try
        {
            while (await err.ReadLineAsync(ct) is { } line)
                if (line.Length > 0) _log?.Invoke($"parec : {line}");
        }
        catch { /* la fin du processus ferme le tube, c'est normal */ }
    }

    private static async Task<bool> FillAsync(Stream s, byte[] buffer, CancellationToken ct)
    {
        var off = 0;
        while (off < buffer.Length)
        {
            int n;
            try { n = await s.ReadAsync(buffer.AsMemory(off), ct); }
            catch (OperationCanceledException) { return false; }
            catch (IOException) { return false; }     // le tube s'est ferme
            if (n == 0) return false;                 // parec s'est arrete
            off += n;
        }
        return true;
    }

    /// <summary>
    /// Le tempo annonce par la fiche du crate.
    ///
    /// Il ne remplace pas la mesure : il recentre la ponderation de l'autocorrelation, qui
    /// continue de chercher. Un disque pousse au fader est suivi malgre sa fiche. Voir
    /// <see cref="IAcceptsCue"/> et <see cref="SpectrumAnalyzer.Amorcer"/>.
    /// </summary>
    public void Amorcer(float bpm) => _analyzer.Amorcer(bpm);
}
