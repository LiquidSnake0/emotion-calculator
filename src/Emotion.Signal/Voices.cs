namespace Emotion.Signal;

/// <summary>
/// Les instruments qui ne frappent pas, suivis registre par registre.
/// </summary>
/// <param name="Low">niveau du grave — agregat des deux premiers registres.</param>
/// <param name="Mid">niveau du medium.</param>
/// <param name="High">niveau de l'aigu.</param>
/// <param name="Levels">niveau de chacun des <see cref="Registers"/> registres.</param>
/// <param name="Pitches">position dans chaque registre, 0 en bas, 1 en haut.</param>
/// <param name="Hits">un bit par registre : celui qui vient d'etre attaque.</param>
public readonly record struct Voices(
    float Low, float Mid, float High,
    bool LowHit, bool MidHit, bool HighHit,
    float LowPitch = 0.5f, float MidPitch = 0.5f, float HighPitch = 0.5f,
    float[]? Levels = null, float[]? Pitches = null, int Hits = 0,
    LaneState[]? Lanes = null, int[]? Labels = null,
    // COMBIEN DE SOURCES SONT REELLEMENT PUBLIEES. Zero tant que la separation n'a rien
    // appris ; ensuite le nombre qu'elle a decouvert, jamais plus que les rangs du paquet.
    int Actives = 0,
    // LE MOTIF DE CHAQUE SOURCE : un bit par case de la mesure, la ou elle monte — le morse
    // a venir, pas le morse passe. Et le verrou : le morceau est su, on ne retouche plus.
    ushort[]? Motifs = null, bool Verrou = false,
    // LE CARACTERE de chaque source, sur la duree : 0 frappe, 1 tient. C'est l'octet qui
    // dit au rendu quel geste donner a quelle source.
    float[]? Caracteres = null,
    // LE DEGRE que joue chaque source dans la gamme de la fiche : 0 tonique … 6, 7 hors
    // gamme, 15 inconnu. Et l'accord du chromagramme avec cette gamme, 0 a 1.
    int[]? Degres = null, float AccordGamme = 0f,
    // LE RELAIS. La platine de chaque source (1 ou 2 : l'identite du disque, stable tant
    // qu'il tourne ; 3 le reste partage pendant un fondu ; 0 inconnu) ; laquelle joue et
    // laquelle entre (0 si aucune) ; le rythme est-il su (le verrou rapide, celui du
    // boom-tchak) ; et le pitch : le tempo qu'on mesure au master rapporte a celui que le
    // cue a appris — 1 si le DJ n'a pas touche au fader.
    int[]? Platines = null, int PlatineJoue = 0, int PlatineEntre = 0,
    bool VerrouRythme = false, float Pitch = 1f)
{
    /// <summary>
    /// Nombre de registres tonals suivis separement.
    ///
    /// TROIS NE SUFFISAIENT PAS, ET LA RAISON EST MUSICALE. Un piano et un saxophone qui
    /// jouent dans la meme octave tombaient dans le meme registre : ils devenaient une
    /// seule grandeur, donc une seule forme, et tout ce que l'oreille distingue entre eux
    /// disparaissait. Le DJ l'a dit ainsi : « a l'oreille je vois tellement de choses et
    /// t'en affiches pas autant ».
    ///
    /// Six bandes d'une octave chacune, de 100 Hz a 6,4 kHz. Assez fin pour que deux
    /// instruments d'un meme morceau tombent rarement ensemble, assez large pour qu'un
    /// seul n'occupe pas trois bandes a lui tout seul.
    ///
    /// <b>Et aucune n'est nommee.</b> On ne decide pas que la bande 3 est un piano — elle
    /// est la bande 3. Ce qui joue dedans change d'un disque a l'autre, et pretendre le
    /// contraire ferait afficher un piano la ou passe un saxophone.
    /// </summary>
    public const int Registers = 6;

    public static readonly Voices None = new(0f, 0f, 0f, false, false, false);

    public bool Any => LowHit || MidHit || HighHit;

    /// <summary>Le registre donne vient-il d'etre attaque.</summary>
    public bool HitAt(int i) => (Hits & (1 << i)) != 0;

    public float LevelAt(int i) => Levels is { } l && i < l.Length ? l[i] : 0f;
    public float PitchAt(int i) => Pitches is { } p && i < p.Length ? p[i] : 0.5f;

    /// <summary>Tout ce que la voie publie, empreinte comprise.</summary>
    public LaneState LaneAt(int i) =>
        Lanes is { } v && i < v.Length ? v[i] : new LaneState(LevelAt(i), PitchAt(i), HitAt(i));

    /// <summary>
    /// Le nom pose sur cette source, ou zero tant qu'elle est anonyme.
    ///
    /// UN ENTIER ET NON UN OCTET, POUR UNE RAISON DE TRANSPORT. Un tableau d'octets est
    /// serialise en base64 par System.Text.Json : le renderer recevait une chaine et
    /// affichait « 1·A » la ou il attendait un numero. Le paquet GPU, lui, garde bien un
    /// octet — c'est le canal JSON qui impose ce choix, pas le format binaire.
    /// </summary>
    public byte LabelAt(int i) => Labels is { } n && i < n.Length ? (byte)n[i] : (byte)0;
}

/// <summary>
/// Suit ce qui joue sans frapper, chaque registre pris a part et calcule a part.
///
/// Le tracker ne calcule plus rien lui-meme : il tient six voies independantes, les fait
/// tourner par le pipeline — qui decide seul si le parallele vaut le coup sur cette
/// machine — puis assemble ce qu'elles ont publie. Cet assemblage, lui, se fait apres que
/// toutes ont fini, donc sur un seul fil : c'est le seul endroit ou les six grandeurs se
/// rencontrent, et il n'y a la aucune concurrence a arbitrer.
/// </summary>
public sealed class VoiceTracker
{
    private const int N = Voices.Registers;

    private readonly SourcePipeline _pipeline;

    // Deux jeux publies a tour de role, pour ne rien allouer par image : le consommateur
    // en ligne a fini de lire l'un avant que l'autre ne soit reecrit. Meme raison que pour
    // les douze bandes du spectre.
    private readonly float[][] _levelPool = [new float[N], new float[N]];
    private readonly float[][] _pitchPool = [new float[N], new float[N]];
    private readonly LaneState[][] _statePool = [new LaneState[N], new LaneState[N]];
    private readonly int[] _labels = new int[N];
    private int _turn;

    public VoiceTracker(int sampleRate, int window)
    {
        var binHz = sampleRate / (float)window;

        // Bandes logarithmiques parce que l'oreille entend des rapports : de 100 a 200 il
        // y a le meme intervalle que de 3200 a 6400.
        var edges = new int[N + 1];
        for (var i = 0; i <= N; i++)
            edges[i] = Math.Max(i + 1, (int)(100f * MathF.Pow(2f, i) / binHz));
        for (var i = 1; i <= N; i++)
            if (edges[i] <= edges[i - 1]) edges[i] = edges[i - 1] + 1;

        var frameSeconds = window / (float)sampleRate;
        var lanes = new ISourceLane[N];
        for (var r = 0; r < N; r++)
            lanes[r] = new RegisterLane(r, edges[r], edges[r + 1], frameSeconds);
        _pipeline = new SourcePipeline(lanes);
    }

    /// <summary>Le pipeline, pour que la sonde puisse rapporter ce qu'il a mesure.</summary>
    public SourcePipeline Pipeline => _pipeline;

    public Voices Feed(ReadOnlySpan<float> spectre)
    {
        _pipeline.Feed(spectre);

        var levels = _levelPool[_turn];
        var pitches = _pitchPool[_turn];
        var states = _statePool[_turn];
        _turn ^= 1;

        var hits = 0;
        var lanes = _pipeline.Lanes;
        for (var r = 0; r < N; r++)
        {
            var state = lanes[r].State;
            states[r] = state;
            levels[r] = state.Level;
            pitches[r] = state.Position;
            if (state.Hit) hits |= 1 << r;
        }

        // Les trois agregats restent publies : le paquet GPU et le rendu s'en servent pour
        // ce qui n'a pas besoin du detail — la masse des graves, par exemple.
        var low = (levels[0] + levels[1]) * 0.5f;
        var mid = (levels[2] + levels[3]) * 0.5f;
        var high = (levels[4] + levels[5]) * 0.5f;

        return new Voices(
            low, mid, high,
            (hits & 0b000011) != 0,
            (hits & 0b001100) != 0,
            (hits & 0b110000) != 0,
            (pitches[0] + pitches[1]) * 0.5f,
            (pitches[2] + pitches[3]) * 0.5f,
            (pitches[4] + pitches[5]) * 0.5f,
            levels, pitches, hits, states, _labels);
    }

    /// <summary>
    /// Pose un nom sur une source, ou l'efface avec zero.
    ///
    /// L'analyse ne nomme rien d'elle-meme : elle transporte. Ce nom vient de la fiche du
    /// crate ou du DJ, et il se pose sur une empreinte que la voie a mise plusieurs
    /// secondes a former — c'est la confiance publiee par la voie qui dit si elle est
    /// prete a le porter.
    /// </summary>
    public void Nommer(int registre, byte nom)
    {
        if ((uint)registre >= N) return;
        _labels[registre] = nom;
        if (_pipeline.Lanes[registre] is RegisterLane lane) lane.Label = nom;
    }

    /// <summary>
    /// Reprend ce qu'on savait deja de ce morceau. Tout ce qui sera entendu ensuite
    /// corrigera ces portraits au lieu de les remplacer.
    /// </summary>
    public void Reprendre(in TrackKnowledge knowledge)
    {
        if (!knowledge.Any) return;

        for (var r = 0; r < N && r < knowledge.Sources.Length; r++)
        {
            if (_pipeline.Lanes[r] is not RegisterLane lane) continue;
            lane.Identity.Load(knowledge.Sources[r]);
            _labels[r] = knowledge.Sources[r].Label;
        }
    }

    /// <summary>Ce qu'on sait a cet instant, pret a etre range pour la prochaine ecoute.</summary>
    public SourcePortrait[] Portraits()
    {
        var p = new SourcePortrait[N];
        for (var r = 0; r < N; r++)
            p[r] = _pipeline.Lanes[r] is RegisterLane lane ? lane.Identity.Save() : default;
        return p;
    }

    /// <summary>
    /// Les voies forment-elles leur portrait, ou se contentent-elles de suivre ?
    ///
    /// On suspend pendant un fondu : le master y entend les deux disques a la fois, et un
    /// portrait forme sur ce melange ne decrirait ni l'un ni l'autre. Ce que le casque
    /// vient de transmettre reste alors intact, et sert immediatement.
    /// </summary>
    public bool Apprend
    {
        set { foreach (var l in _pipeline.Lanes) if (l is RegisterLane r) r.Learning = value; }
    }

    /// <summary>Le portrait d'une voie, pour la sonde et le reglage.</summary>
    public SourceIdentity PortraitDe(int registre) =>
        _pipeline.Lanes[registre] is RegisterLane lane ? lane.Identity : new SourceIdentity();

    /// <summary>Ce que chaque voie sait d'elle-meme en ce moment.</summary>
    public LaneState EtatDe(int registre) =>
        (uint)registre < N ? _pipeline.Lanes[registre].State : LaneState.Silent;
}
