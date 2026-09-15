namespace Emotion.Signal;

/// <summary>
/// Une voie d'analyse : une source, isolee, qui ne touche qu'a son propre etat.
///
/// POURQUOI UNE INTERFACE PLUTOT QU'UNE BOUCLE SUR DES TABLEAUX.
///
/// L'ancienne version tenait les six registres dans huit tableaux paralleles indexes par
/// rang. Cela marche tant qu'un seul fil s'en occupe, mais rien dans cette forme ne dit
/// qu'un registre est independant des autres : il faut relire toute la boucle pour s'en
/// convaincre, et la moindre variable commune s'y glisse sans qu'on la remarque — il y en
/// avait une, <c>hits</c>, et elle aurait perdu des attaques des la premiere execution
/// parallele.
///
/// Une voie qui possede son etat rend cette independance <b>visible et verifiable</b> :
/// ce qu'elle ne peut pas atteindre, elle ne peut pas le corrompre. La separation des
/// donnees remplace la synchronisation, et c'est ce qui permet aux six de tourner
/// ensemble sans un seul verrou.
///
/// C'est aussi ce qui rend le pipeline ouvert a l'extension : ajouter une septieme source
/// n'oblige a modifier aucune des six autres.
/// </summary>
public interface ISourceLane
{
    /// <summary>Rang de la voie, du grave a l'aigu. Fixe sa zone dans le paquet GPU.</summary>
    int Rank { get; }

    /// <summary>Une image de spectre. La voie n'y lit que ce qui la concerne.</summary>
    void Feed(ReadOnlySpan<float> spectrum);

    /// <summary>Ce que la voie a a dire d'elle-meme, apres analyse.</summary>
    LaneState State { get; }

    void Reset();
}

/// <summary>
/// Ce qu'une voie publie.
///
/// Les trois premieres grandeurs decrivent l'instant et sont justes des la premiere image.
/// Les trois dernieres decrivent la source elle-meme et se forment lentement — la
/// confiance dit ou en est ce portrait. Les unes n'attendent pas les autres : c'est ce qui
/// permet au paquet de partir a cadence fixe pendant que chaque source murit a son rythme.
/// </summary>
/// <param name="Level">activation, 0 a 1.</param>
/// <param name="Position">ou joue la source dans son registre, 0 en bas, 1 en haut.</param>
/// <param name="Hit">une attaque vient d'etre constatee.</param>
/// <param name="Heard">a-t-on assez ecoute cette source, 0 a 1. Une question de duree.</param>
/// <param name="Sharpness">la bande porte-t-elle un seul timbre. Une propriete du disque.</param>
/// <param name="Brightness">brillance moyenne de la source.</param>
/// <param name="Texture">raie franche a souffle.</param>
/// <param name="Pique">
/// A quel point la source monte d'un coup, 0 a 1. Une corde pincee approche un, un souffle
/// approche zero. Voir <see cref="SourceEnvelope"/>.
/// </param>
/// <param name="Tenue">
/// A quel point elle reste au niveau atteint, 0 a 1. Un souffle approche un, un pizzicato
/// approche zero.
///
/// AVEC <paramref name="Pique"/>, ELLES DISENT CE QU'AUCUNE AUTRE GRANDEUR NE DISAIT : une
/// corde pincee et un souffle produisent le meme niveau moyen et la meme hauteur, et le
/// rendu leur donnait donc le meme mouvement. C'est le couple des deux qui les separe —
/// pique fort et tenue courte pour un pizzicato, pique nul et tenue pleine pour un vent,
/// pique fort et tenue moyenne pour un piano, c'est-a-dire « une frappe suivie d'une onde
/// courte ou longue ».
/// </param>
/// <param name="Retrait">
/// Depuis combien de secondes elle s'est tue. Zero si elle joue.
///
/// « Quand le kick est en retrait pendant un moment, on est cense le savoir. » Une source
/// absente ne disparait pas de l'ecran : elle s'y montre eteinte, avec le temps qu'elle a
/// passe a se taire.
/// </param>
public readonly record struct LaneState(
    float Level, float Position, bool Hit,
    float Heard = 0f, float Sharpness = 0f,
    float Brightness = 0.5f, float Texture = 0.5f,
    float Pique = 0f, float Tenue = 0f, float Retrait = 0f,
    // LA PART DOMINANTE : la fraction de l'energie de la source qui vit dans des cases
    // dont elle est proprietaire. Le reste est discret — partage, entendu en cachette.
    float Dominance = 0f,
    // LA PLATINE : 1 ou 2, l'identite du disque tant qu'il tourne — elle ne change pas au
    // relais, contrairement a « celui qui joue / celui qui entre ». 3 = partage (le reste
    // pendant un fondu), 0 = inconnu.
    int Platine = 0)
{
    public static LaneState Silent => new(0f, 0.5f, false);

    /// <summary>Ce qu'il faut pour poser un nom : avoir assez ecoute, et une bande nette.</summary>
    public float Confidence => Heard * Sharpness;
}

/// <summary>
/// La voie d'un registre : une octave du spectre, suivie a part.
///
/// Elle porte exactement ce que le registre exige et rien de plus — ses bornes, son
/// maximum recent, son contour, son detecteur d'attaque. Aucune de ces grandeurs n'est
/// partagee avec un autre registre, ce qui est la condition pour que les six tournent en
/// meme temps.
/// </summary>
public sealed class RegisterLane : ISourceLane
{
    /// <summary>
    /// Duree d'une fenetre, en secondes. Elle vient du taux reel : une constante faisait
    /// glisser le contour melodique 8,8 % trop vite sur un signal a 44,1 kHz.
    /// </summary>
    private readonly float _frameSeconds;

    private readonly int _lo;
    private readonly int _hi;
    private readonly Damper _pitch;
    private readonly OnsetDetector _onset;

    /// <summary>
    /// Ce que la voie apprend d'elle-meme au fil du morceau. Elle en est proprietaire :
    /// une identite qui vivrait dans une table commune redeviendrait un point de
    /// rencontre entre les six.
    /// </summary>
    private readonly SourceIdentity _identity = new();

    private float _peak = 1e-3f;
    private float _target = 0.5f;
    private float _level;
    private float _previous;

    public int Rank { get; }
    public LaneState State { get; private set; } = LaneState.Silent;

    public RegisterLane(int rank, int lo, int hi, float frameSeconds)
    {
        Rank = rank;
        _lo = lo;
        _hi = hi;
        _frameSeconds = frameSeconds;

        // Le contour glisse d'autant plus vite que le registre est haut : une note aigue
        // change plus souvent qu'une note grave.
        _pitch = new Damper(6f + rank * 1.2f, 0.5f);

        // Huit fenetres, soit 170 ms : deux notes par temps restent distinctes a 87 BPM,
        // la ou la limite des percussions en autoriserait une seule.
        _onset = new OnsetDetector(minGap: 8);
    }

    /// <summary>
    /// La voie apprend-elle de ce qu'elle entend, ou se contente-t-elle de le suivre ?
    ///
    /// PENDANT UN FONDU, ON SUIT SANS APPRENDRE.
    ///
    /// Les deux disques sonnent ensemble, et le master entend une somme qui n'existe dans
    /// aucun des deux : deux basses se superposent, deux nappes se recouvrent. Un portrait
    /// forme la-dessus ne decrirait ni l'un ni l'autre — et il ecraserait justement celui
    /// que le casque vient de transmettre, qui lui est propre et deja forme.
    ///
    /// Le rendu, lui, ne s'interrompt pas : les niveaux, les contours et les attaques
    /// continuent de partir a cadence pleine. Seule la formation du portrait est suspendue,
    /// et elle reprend d'elle-meme une fois le fader arrive au bout.
    /// </summary>
    public bool Learning { get; set; } = true;

    public void Feed(ReadOnlySpan<float> spectrum)
    {
        var hi = Math.Min(_hi, spectrum.Length);
        if (hi <= _lo)
        {
            State = new LaneState(0f, _pitch.Value, false,
                                  _identity.Heard, _identity.Sharpness,
                                  _identity.Brightness, _identity.Texture);
            return;
        }

        // OU JOUE CE REGISTRE, ET PAS SEULEMENT COMBIEN.
        //
        // Un niveau ne decrit aucun mouvement : quand une melodie monte, une bande baisse
        // et sa voisine monte — deux faits independants dont aucun ne porte le geste. Le
        // centre de gravite, lui, se deplace, et une forme peut le suivre.
        double sum = 0, weighted = 0;
        for (var i = _lo; i < hi; i++)
        {
            var v = spectrum[i];
            sum += v;
            weighted += v * MathF.Log2(MathF.Max(i, 1));
        }

        var raw = (float)(sum / (hi - _lo));

        // Normalisation sur le maximum recent du registre : un instrument discret et un
        // instrument pousse doivent tous deux se voir.
        _peak = MathF.Max(raw, _peak * 0.9995f);
        _level = Clamp01(raw / MathF.Max(_peak, 1e-4f));

        if (sum > 1e-6)
        {
            var bottom = MathF.Log2(MathF.Max(_lo, 1));
            var span = MathF.Log2(MathF.Max(hi - 1, 2)) - bottom;
            var pos = span > 1e-3f ? (float)(weighted / sum - bottom) / span : 0.5f;

            // ON NE SUIT QUE CE QU'ON ENTEND. Un centre de gravite calcule sur un registre
            // presque muet saute au gre du bruit de fond, puis saute encore au retour du
            // son. La position d'une note qui n'existe pas n'a pas a etre tenue a jour.
            var lively = 0.04f + 0.16f * _level;
            _target += (Clamp01(pos) - _target) * lively;
        }

        // Puis un ressort, et non une seconde moyenne : une moyenne exponentielle arrive
        // toujours en retard et sans elan, ce qui fait qu'un mouvement parait mou. Un
        // ressort a une vitesse, donc de l'inertie.
        var position = _pitch.Feed(_target, _frameSeconds);

        // La montee, et non le niveau : une note tenue ne doit pas declencher en
        // permanence, seule son attaque compte.
        var hit = _onset.Feed(MathF.Max(0f, _level - _previous));
        _previous = _level;

        // Le portrait se forme ici, sur le meme fil que le reste de la voie : il ne coute
        // qu'un passage sur la bande, et il ne sort jamais de la voie.
        if (Learning) _identity.Feed(spectrum, _lo, hi, _level);

        State = new LaneState(_level, position, hit,
                              _identity.Heard, _identity.Sharpness,
                              _identity.Brightness, _identity.Texture);
    }

    /// <summary>Le portrait de la source, pour la sonde.</summary>
    public SourceIdentity Identity => _identity;

    /// <summary>Le nom pose sur cette source, ou zero. Ecrit de l'exterieur, transporte tel quel.</summary>
    public byte Label
    {
        get => _identity.Label;
        set => _identity.Label = value;
    }

    public void Reset()
    {
        _peak = 1e-3f;
        _target = 0.5f;
        _level = 0f;
        _previous = 0f;
        _pitch.Reset(0.5f);
        _identity.Reset();
        State = LaneState.Silent;
    }

    private static float Clamp01(float x) => x < 0f ? 0f : x > 1f ? 1f : x;
}
