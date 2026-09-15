namespace Emotion.Signal;

/// <summary>
/// Deux entrees : le master, qui sort en salle, et le cue, ce que le DJ ecoute au
/// casque pendant qu'il cale son prochain disque.
///
/// La seconde entree est ce qui permet au visuel de <b>suivre une transition au lieu de
/// l'annoncer</b>. Avec le master seul, on ne peut que basculer sur commande ; avec les
/// deux, on mesure combien du prepare est deja passe dans le melange, et la projection
/// glisse au rythme du fader.
///
/// C'est aussi ce qui prepare le systeme : pendant tout le beatmatch, l'analyseur du cue
/// tourne deja sur le disque a venir. Ses seuils sont donc cales et son tempo verrouille
/// avant meme que le public en entende la premiere note.
///
/// <b>Le cue est un analyseur de plein droit, pas un capteur.</b> Il a son propre
/// detecteur d'attaques, son propre estimateur de tempo, sa propre analyse harmonique —
/// tout ce que possede le master. Pendant les huit ou seize mesures du beatmatch, il
/// accroche donc le tempo et le profil de hauteurs du disque a venir.
///
/// A mi-fondu, il passe le relais : le master <b>reprend</b> ce tempo comme point de
/// depart, au lieu de repartir de rien pendant le passage le plus visible du set — un
/// analyseur met une a deux secondes a accrocher.
///
/// Mais ce n'est qu'un point de depart, et la nuance est essentielle. <b>Le master a
/// bien a decouvrir</b>, pour deux raisons : le pitch a bouge pendant le beatmatch,
/// c'est meme le but du geste, et l'EQ de la table modifie le spectre entre le casque
/// et la sortie. Ce qui joue en salle n'est donc jamais tout a fait ce que le cue a
/// entendu. Le relais amorce, il ne verrouille pas : le master continue de suivre la
/// continuite du son qui sort reellement, et sa propre mesure remplace l'amorce en
/// quelques mesures.
///
/// Le master mene la cadence et s'abonne au cue : c'est lui qui emet les images, en y
/// joignant la part du prepare deja passee dans le melange. Si le cue s'arrete — casque
/// debranche, table eteinte — le master continue seul avec un fondu mesure a zero.
/// </summary>
public sealed class DualAudioSource : IAudioSource
{
    private readonly IAudioSource _master;
    private readonly IAudioSource _cue;
    private readonly BlendEstimator _blend = new();

    private volatile float[] _lastCueBands = new float[VisualFrame.BandCount];
    private VisualFrame _cueFrame;
    private readonly Lock _cueGate = new();

    /// <summary>
    /// A quel niveau de fondu le relais se fait. A mi-chemin : plus tot, le morceau qui
    /// arrive ne domine pas encore et le master se calerait sur ce qui va disparaitre ;
    /// plus tard, on aurait laisse passer le moment ou l'aide sert.
    /// </summary>
    private const float HandoverAt = 0.5f;

    /// <summary>
    /// LE RELAIS COMPLET, EN TROIS TEMPS, AU RYTHME DU FADER.
    ///
    ///   accueil   des que le disque qui entre s'entend dans le melange, ses gabarits, ses
    ///             motifs et son caractere rejoignent ceux du disque qui joue dans le suivi
    ///             du master. Deux portraits, un seul suivi : l'image suit le fondu.
    ///   tempo     a mi-fondu, comme avant : le master reprend le tempo appris au cue.
    ///   retrait   quand le fondu est fini, le disque qui sortait est retire du suivi, celui
    ///             qui est entre devient le disque qui joue, et le cue se remet a zero pour
    ///             le disque suivant.
    ///
    /// Le master ne forme jamais de portrait lui-meme : il ne fait que suivre ce qu'on lui
    /// transmet, et mesurer ce qui a bouge — le pitch, les niveaux.
    /// </summary>
    private const float AccueilAt = 0.15f;
    private const float RetraitAt = 0.9f;

    private int _phase;   // 0 rien, 1 accueilli, 2 tempo relaye, 3 retire
    private bool _handedOver;

    /// <summary>
    /// La derniere analyse du cue : tempo, harmonie, registres du disque en preparation.
    /// Elle alimente le bandeau du DJ — jamais la projection, qui ne doit rien laisser
    /// voir du beatmatch.
    /// </summary>
    public VisualFrame CueFrame
    {
        get { lock (_cueGate) return _cueFrame; }
        private set { lock (_cueGate) _cueFrame = value; }
    }

    public DualAudioSource(IAudioSource master, IAudioSource cue)
    {
        _master = master;
        _cue = cue;
        // Les roles : le cue apprend, le master joue. Le master ne formera jamais de portrait
        // sur ce qu'il entend — c'est une somme de deux disques.
        if (master.Analyzer is { } am) am.Role = RoleAnalyseur.Master;
        if (cue.Analyzer is { } ac) ac.Role = RoleAnalyseur.Cue;
    }

    public string Name => $"{_master.Name} + cue {_cue.Name}";

    /// <summary>
    /// Les deux faces, pour que chacune accumule sa propre connaissance.
    ///
    /// LA FACE CALEE APPREND AUTANT QUE CELLE QUI JOUE, ET SOUVENT PLUS.
    ///
    /// C'est au casque que l'aiguille repasse : caler une face veut dire revenir au debut
    /// plusieurs fois pour verifier le tempo. Chacun de ces passages est une ecoute de plus
    /// du meme extrait, et leur cumul depasse largement une seule ecoute continue. Ne faire
    /// apprendre que le master jetterait exactement la matiere la plus abondante, et
    /// obligerait a tout redecouvrir au moment de la transition — c'est-a-dire au seul
    /// moment ou l'on n'a pas le temps.
    /// </summary>
    public IAudioSource Master => _master;

    /// <summary>Celui de la platine qui sort, jamais celui du casque.</summary>
    public SpectrumAnalyzer? Analyzer => _master.Analyzer;
    public IAudioSource Cue => _cue;

    /// <summary>
    /// Remet la mesure a zero : nouveau disque au casque, et un nouveau relais a venir.
    /// </summary>
    public void ResetBlend()
    {
        _blend.Reset();
        _handedOver = false;
        _phase = 0;
    }

    /// <summary>Ou en est le relais, pour le journal et la sonde : 0 rien, 1 accueilli, 2 tempo, 3 retire.</summary>
    public int Phase => _phase;

    public async IAsyncEnumerable<VisualFrame> ReadAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);

        // Le cue tourne dans sa propre boucle et depose son dernier profil de bandes.
        // On ne synchronise pas les deux flux a l'echantillon pres : la correlation
        // porte sur une seconde et demie, un decalage de quelques dizaines de
        // millisecondes entre les deux cartes n'a aucun effet sur elle.
        var cueLoop = Task.Run(async () =>
        {
            try
            {
                await foreach (var f in _cue.ReadAsync(stop.Token))
                {
                    _lastCueBands = f.Bands;
                    CueFrame = f;
                }
            }
            catch (OperationCanceledException) { }
        }, stop.Token);

        try
        {
            await foreach (var frame in _master.ReadAsync(stop.Token))
            {
                var blend = _blend.Feed(frame.Bands, _lastCueBands);

                // L'ACCUEIL : le disque qui entre s'entend, ses regles passent au master.
                if (_phase == 0 && blend >= AccueilAt
                    && _cue.Analyzer is { } ac0 && _master.Analyzer is { } am0 && ac0.Separation.Pret)
                {
                    am0.Accueillir(ac0.Empreinte());
                    _phase = 1;
                }

                // Le passage de relais du tempo, une seule fois par transition.
                if (!_handedOver && blend >= HandoverAt)
                {
                    var cue = CueFrame;
                    if (cue.Bpm is { } bpm && _master is PulseAudioSource p)
                    {
                        p.AdoptTempo(bpm, frame.T);
                        _handedOver = true;
                    }
                    if (_phase == 1) _phase = 2;
                }

                // LE RETRAIT : le fondu est fini, le disque qui sortait quitte le suivi et le
                // cue est libre pour le suivant.
                if (_phase is 1 or 2 && blend >= RetraitAt && _master.Analyzer is { } am3)
                {
                    am3.Retirer();
                    _cue.NewTrack();
                    _phase = 3;
                }

                // PENDANT LE FONDU, LE MASTER SUIT MAIS N'APPREND PLUS.
                //
                // Les deux disques sonnent ensemble : ce qu'il entend est une somme qui
                // n'existe dans aucun des deux. Former un portrait la-dessus ecraserait
                // celui que le casque vient de transmettre, qui lui est propre et deja
                // constitue. Le rendu ne s'interrompt pas pour autant — seule la formation
                // du portrait attend que le fader soit arrive au bout.
                if (_master is PulseAudioSource maitre) maitre.Fondu = blend;

                yield return frame with { Blend = blend };
            }
        }
        finally
        {
            stop.Cancel();
            try { await cueLoop; } catch { }
        }
    }
}
