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
///
/// ET IL NE S'ARME QU'APRES UN SILENCE. Avec des voies prises apres le fader, la voie qui
/// devient le cue apres un retrait porte encore le disque qui sort : elle est dans le
/// master, et le filet — juste en principe — renvoyait le cue sur la voie du master, d'ou
/// un relais de plus par passage (17 faux relais sur set-2, le 26 septembre 2026). Le
/// filet attend donc que la voie du cue se soit tue une seconde depuis le dernier relais :
/// le disque sortant est arrete, ce qui joue ensuite sur cette voie est bien le suivant.
///
/// UN SECOND FILET A ETE ESSAYE ET RETIRE : « la voie du cue est muette cinq secondes
/// pendant que l'autre joue, donc ce n'est pas le cue ». Une voie muette pendant que
/// l'autre joue est l'attente normale du prochain disque ; le filet en faisait un relais
/// toutes les huit secondes — 163 relais pour 26 gestes sur le set de 17 h. Ajoute a chaud
/// sur un cas vu une fois, sans mesure : exactement ce que ContinuityWatch avait deja
/// appris.
/// </summary>
public sealed class CueAlternant : IAudioSource, ILearnsTracks, IAcceptsCue
{
    /// <summary>Au-dela, le cue est dans le master : il ne peut pas etre le disque prepare.</summary>
    public const float DedansAt = 0.8f;

    /// <summary>Trois secondes d'images d'analyse (47 par seconde) avant de conclure.</summary>
    public const int ImagesDedans = 141;

    /// <summary>Une seconde de voie muette (47 images) depuis le dernier relais, et le filet s'arme.</summary>
    public const int ImagesSilence = 47;
    public const float Muet = 0.02f;
    private readonly float[] _niveau = new float[2];
    private int _imagesMuettes;
    private bool _arme = true;

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

    /// <summary>Le filet peut permuter : au depart, ou apres qu'une voie du cue s'est tue depuis le dernier relais.</summary>
    public bool FiletArme => _arme;

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
        // La voie nouvelle porte encore le disque qui sort : le filet attend qu'elle se taise.
        _arme = false;
        _imagesMuettes = 0;
    }

    /// <summary>
    /// Le filet, une image a la fois : le fondu du cue dans le master, et si un relais
    /// est engage. Rend vrai quand la voie a ete permutee — l'appelant remet alors sa
    /// mesure de fondu a zero, elle decrivait l'autre voie.
    /// </summary>
    public bool Observer(float blend, bool relaisEnCours)
    {
        if (!_arme)
        {
            _imagesMuettes = _niveau[_active] < Muet ? _imagesMuettes + 1 : 0;
            if (_imagesMuettes >= ImagesSilence) { _arme = true; _imagesMuettes = 0; }
            _imagesDedans = 0;
            return false;
        }

        if (relaisEnCours) { _imagesDedans = 0; return false; }
        _imagesDedans = blend >= DedansAt ? _imagesDedans + 1 : 0;
        if (_imagesDedans < ImagesDedans) return false;
        Basculer($"filet : la voie {Voie} est dans le master, ce n'est pas le cue");
        return true;
    }

    /// <summary>Le niveau de chaque voie, tel que sa derniere image le dit (pour les tests et le filet).</summary>
    public void Niveau(int voie, float rms) => _niveau[voie - 1] = rms;

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
                    {
                        _niveau[index] = f.Rms;
                        if (index == _active) file.Writer.TryWrite(f);
                    }
                }
                catch (OperationCanceledException) { }
                catch (Exception e) { _log?.Invoke($"voie {index + 1} : {e.Message}"); }
            }, stop.Token);
        }

        // Quand les deux voies se sont tues — parec absent, peripheriques disparus — le flux
        // se termine au lieu d'attendre en silence.
        _ = Task.WhenAll(lectures).ContinueWith(_ => file.Writer.TryComplete(), TaskScheduler.Default);

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
