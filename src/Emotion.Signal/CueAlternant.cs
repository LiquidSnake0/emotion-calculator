using System.Threading.Channels;

namespace Emotion.Signal;

/// <summary>
/// Le cue quand la table donne les voies, pas le casque.
///
/// Une DJM sur USB remonte chaque voie avant son fader, jamais la sortie casque. Le disque
/// que le DJ prepare est donc quelque part dans ces voies : c'est celle qui joue sans etre
/// dans le master. Avec deux platines, on n'a meme pas a chercher — <b>apres chaque
/// relais, le cue est sur l'autre voie</b>. A joue sur la 1, B se prepare sur la 2 ; B
/// prend, la 2 est le master, le prochain disque se pose forcement sur la 1.
///
/// Le crate dit QUEL disque est au casque (la fiche) ; cette classe dit sur QUELLE voie.
/// Les deux ne se confondent pas : le crate n'a pas a connaitre la table.
///
/// Les deux voies sont analysees en permanence, chacune par son propre analyseur en role
/// Cue : celle qui est active nourrit le master, l'autre attend son tour. Basculer ne
/// coute rien, puisque l'autre analyseur tournait deja.
///
/// LE FILET. Au depart, ou si un relais s'est fait sans que la machine le voie, le cue
/// peut se retrouver sur la voie du master. Ca se mesure : le fondu du cue dans le master
/// vaut alors 1, tout de suite, et y reste. Trois secondes a plus de 0,8 hors relais, et
/// l'on permute. Un vrai fondu ne ressemble pas a ca : il monte depuis zero, et le relais
/// est engage avant d'atteindre 0,8.
/// </summary>
public sealed class CueAlternant : IAudioSource, ILearnsTracks, IAcceptsCue
{
    /// <summary>Au-dela, le cue est dans le master : il ne peut pas etre le disque prepare.</summary>
    public const float DedansAt = 0.8f;

    /// <summary>Trois secondes d'images d'analyse (47 par seconde) avant de conclure.</summary>
    public const int ImagesDedans = 141;

    private readonly IAudioSource[] _voies;
    private readonly Action<string>? _log;
    private volatile int _active;
    private int _imagesDedans;

    public CueAlternant(IAudioSource voie1, IAudioSource voie2, Action<string>? log = null)
    {
        _voies = [voie1, voie2];
        _log = log;
        foreach (var v in _voies)
            if (v.Analyzer is { } a) a.Role = RoleAnalyseur.Cue;
    }

    /// <summary>La voie qui porte le cue en ce moment, 1 ou 2.</summary>
    public int Voie => _active + 1;

    /// <summary>Combien de fois le cue a change de voie depuis le depart.</summary>
    public int Bascules { get; private set; }

    public IAudioSource Active => _voies[_active];

    public string Name => $"voie {Voie} de ({_voies[0].Name} | {_voies[1].Name})";

    public SpectrumAnalyzer? Analyzer => Active.Analyzer;

    /// <summary>Le cue passe sur l'autre voie. L'analyseur qui s'y trouve garde ce qu'il sait.</summary>
    public void Basculer(string pourquoi)
    {
        _active = 1 - _active;
        _imagesDedans = 0;
        Bascules++;
        _log?.Invoke($"cue → voie {Voie} ({pourquoi})");
    }

    /// <summary>
    /// Un autre disque commence au casque : c'est ce que le relais dit apres le retrait.
    /// Le cue change de voie, et l'analyseur de cette voie repart de zero — ce qu'il
    /// entendait jusque-la etait le disque qui vient de passer en salle.
    /// </summary>
    public void NewTrack()
    {
        Basculer("apres le relais, le prochain disque est sur l'autre voie");
        Active.NewTrack();
    }

    /// <summary>
    /// Le filet, une image a la fois : le fondu du cue dans le master, et si un relais
    /// est engage. Rend vrai quand la voie a ete permutee — l'appelant remet alors sa
    /// mesure de fondu a zero, elle decrivait l'autre voie.
    /// </summary>
    public bool Observer(float blend, bool relaisEnCours)
    {
        if (relaisEnCours) { _imagesDedans = 0; return false; }
        _imagesDedans = blend >= DedansAt ? _imagesDedans + 1 : 0;
        if (_imagesDedans < ImagesDedans) return false;
        Basculer($"filet : la voie {Voie} est dans le master, ce n'est pas le cue");
        return true;
    }

    public void Amorcer(float bpm) { if (Active is IAcceptsCue a) a.Amorcer(bpm); }

    public void Resume(in TrackKnowledge knowledge) { if (Active is ILearnsTracks l) l.Resume(knowledge); }

    public TrackKnowledge Park(string id, in TrackKnowledge previous) =>
        Active is ILearnsTracks l ? l.Park(id, previous) : previous;

    /// <summary>
    /// Les deux voies lisent en meme temps ; seules les images de la voie active sortent.
    /// La file est bornee et jette le plus ancien : une image en retard ne vaut rien.
    /// </summary>
    public async IAsyncEnumerable<VisualFrame> ReadAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var file = Channel.CreateBounded<VisualFrame>(new BoundedChannelOptions(8)
        {
            FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true,
        });

        var lectures = new Task[_voies.Length];
        for (var i = 0; i < _voies.Length; i++)
        {
            var index = i;
            lectures[i] = Task.Run(async () =>
            {
                try
                {
                    await foreach (var f in _voies[index].ReadAsync(stop.Token))
                        if (index == _active) file.Writer.TryWrite(f);
                }
                catch (OperationCanceledException) { }
                catch (Exception e) { _log?.Invoke($"voie {index + 1} : {e.Message}"); }
            }, stop.Token);
        }

        try
        {
            await foreach (var f in file.Reader.ReadAllAsync(stop.Token))
                yield return f;
        }
        finally
        {
            stop.Cancel();
            try { await Task.WhenAll(lectures); } catch { }
        }
    }
}
