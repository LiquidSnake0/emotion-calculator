namespace Emotion.Signal;

/// <summary>
/// Rejoue un enregistrement <b>au rythme reel</b>, avec les memes horodatages que la
/// capture en direct.
///
/// POURQUOI ELLE EXISTE, ET CE QU'ELLE ISOLE.
///
/// Un ecart tenace separait les deux chemins. Sur un morceau du crate, la sonde hors ligne
/// trouve 676 a 692 ms d'un bout a l'autre du fichier — un tempo stable a 87 BPM. Le meme
/// moteur, alimente par la carte son, publiait 113 BPM pendant treize pour cent du temps,
/// par episodes tenus de vingt a trente secondes.
///
/// Deux causes possibles ont ete ecartees par la mesure. Le son n'est pas en cause :
/// enregistrer ce que <c>parec</c> delivre, avec ses parametres exacts, puis passer cet
/// enregistrement dans la sonde redonne 681 a 687 ms. L'algorithme non plus, puisque c'est
/// le meme.
///
/// Restait la <b>cadence</b>. La sonde avale les fenetres aussi vite qu'elle peut et les
/// date au compte d'echantillons ; le direct les recoit au rythme de la carte son et les
/// date a l'horloge murale. Cette source reproduit le second regime sur une matiere dont on
/// connait la reponse — meme rythme, memes horodatages, mais aucune perte possible.
///
/// Elle sert aussi, au-dela du diagnostic, a rejouer un set enregistre dans toute la chaine
/// sans materiel : ce que la sonde ne permet pas, puisqu'elle court-circuite le serveur.
///
/// AVEC UNE <see cref="HorlogeRejeu"/>, PLUS DE RYTHME REEL : les fenetres sont datees au
/// compte d'echantillons et lues aussi vite que la chaine les avale, mais en pas cadence
/// avec les autres fichiers de la meme session — le Rec Out et les deux voies d'une table,
/// enregistres ensemble, se rejouent ensemble. C'est ce qui fait d'une session studio un
/// fixture : une heure de set se rejoue en quelques minutes, a l'identique, avec le relais.
///
/// LE FICHIER SE LIT PAR TRANCHES D'UNE MINUTE. Une session fait une heure a 48 kHz ; trois
/// fichiers entiers en flottants, c'est deux gigaoctets, et le portable n'en a pas quatre de
/// libres.
/// </summary>
public sealed class WavAudioSource : IAudioSource, ILearnsTracks, IAcceptsCue, IMaitre
{
    private readonly string _path;
    private readonly SpectrumAnalyzer _analyzer;
    private readonly bool _boucle;
    private readonly HorlogeRejeu? _horloge;
    private readonly int _rang;

    public WavAudioSource(string path, bool separate = false, bool boucle = true, HorlogeRejeu? horloge = null)
    {
        _path = path;
        _boucle = boucle;
        var (_, rate) = Wav.ReadMono(path, 0, 0.1);
        SampleRate = rate;
        _analyzer = new SpectrumAnalyzer(rate, separate);
        _horloge = horloge;
        _rang = horloge?.Inscrire() ?? -1;
    }

    public int SampleRate { get; }

    public string Name => $"fichier {Path.GetFileName(_path)}";

    public SpectrumAnalyzer Analyzer => _analyzer;

    public void Resume(in TrackKnowledge knowledge) => _analyzer.Reprendre(knowledge);

    public TrackKnowledge Park(string id, in TrackKnowledge previous) =>
        _analyzer.Connaissance(id, previous);

    public void Amorcer(float bpm) => _analyzer.Amorcer(bpm);

    public void NewTrack() => _analyzer.NewTrack();

    public void AdoptTempo(float bpm, long tMs) => _analyzer.AdoptTempo(bpm, tMs);

    public float Fondu { set => _analyzer.Fondu = value; }

    /// <summary>Une minute par tranche : un multiple de la fenetre, pour que rien ne se perde aux coutures.</summary>
    private const int Tranche = SpectrumAnalyzer.Window * 2800;

    public async IAsyncEnumerable<VisualFrame> ReadAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        const int hop = SpectrumAnalyzer.Window;
        var start = DateTime.UtcNow;
        long lus = 0;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                long depuis = 0;
                while (!ct.IsCancellationRequested)
                {
                    var (mono, _) = Wav.ReadMono(_path, depuis / (double)SampleRate, Tranche / (double)SampleRate);
                    for (var i = 0; i + hop <= mono.Length && !ct.IsCancellationRequested; i += hop)
                    {
                        // La date de la fenetre au compte d'echantillons, tours compris.
                        var tEch = (long)(lus * 1000.0 / SampleRate);
                        lus += hop;
                        long t;
                        if (_horloge is not null)
                        {
                            await _horloge.Attendre(_rang, tEch, ct);
                            t = tEch;
                        }
                        else
                        {
                            // Le rythme reel : on attend que la fenetre soit « arrivee ». Sans cela on
                            // rejouerait le morceau cent fois plus vite et l'on ne testerait rien.
                            var retard = tEch - (DateTime.UtcNow - start).TotalMilliseconds;
                            if (retard > 1) await Task.Delay((int)retard, ct);
                            // Meme base de temps que la capture : l'horloge murale, pas le compte
                            // d'echantillons. C'est precisement la variable qu'on veut isoler.
                            t = (long)(DateTime.UtcNow - start).TotalMilliseconds;
                        }
                        yield return _analyzer.Analyze(mono.AsSpan(i, hop), t);
                    }
                    if (mono.Length < Tranche) break;
                    depuis += Tranche;
                }
                if (!_boucle) yield break;
            }
        }
        finally
        {
            _horloge?.Terminer(_rang);
        }
    }
}
