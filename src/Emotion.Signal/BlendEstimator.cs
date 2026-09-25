namespace Emotion.Signal;

/// <summary>
/// Mesure quelle part du morceau prepare est deja passee dans le master.
///
/// <b>C'est ce qui remplace le bouton de bascule.</b> Jusqu'ici la projection changeait
/// sur une commande : le DJ appuyait, le mur basculait d'un coup. Or une transition de
/// DJ n'est pas un instant, c'est un geste — le fader monte pendant huit ou seize
/// mesures. Le visuel doit suivre ce geste, pas l'annoncer.
///
/// Le principe : on dispose du cue <b>isole</b>, le disque B seul au casque, et du
/// master <b>melange</b>, A plus B selon la position du fader. Si B n'est pas encore
/// dans le master, les deux spectres n'ont aucune raison de se ressembler. A mesure que
/// le fader monte, le master contient de plus en plus de B, et la ressemblance croit.
///
/// On ne mesure donc pas la position du fader — on mesure son effet, ce qui est bien
/// plus juste : un fader a mi-course sur un morceau discret ne fait pas la meme chose
/// qu'a mi-course sur un morceau massif.
/// </summary>
public sealed class BlendEstimator
{
    /// <summary>
    /// Longueur de la fenetre de comparaison. Une seconde et demie : assez pour que la
    /// correlation ne saute pas sur un coup de caisse commun aux deux disques, assez
    /// court pour que le visuel suive un fondu de huit mesures.
    /// </summary>
    private const int History = 70;

    private readonly float[,] _master = new float[History, VisualFrame.BandCount];
    private readonly float[,] _cue = new float[History, VisualFrame.BandCount];
    private int _n;
    private float _smoothed;

    /// <summary>
    /// Part du prepare dans le master, 0 a 1. Lissee : la valeur brute tremble d'une
    /// fenetre a l'autre, et un visuel qui tremble se voit depuis le fond de la salle.
    /// </summary>
    public float Blend => _smoothed;

    /// <summary>La fenetre est pleine : la valeur decrit bien une seconde et demie, pas un depart a zero.</summary>
    public bool Pret => _n >= History;

    /// <summary>
    /// Nourrit l'estimateur des deux profils de bandes de la meme instant.
    /// </summary>
    public float Feed(float[] master, float[] cue)
    {
        var slot = _n % History;
        for (var b = 0; b < VisualFrame.BandCount; b++)
        {
            _master[slot, b] = master[b];
            _cue[slot, b] = cue[b];
        }
        if (_n < int.MaxValue) _n++;

        if (_n < History) return _smoothed;

        var raw = Correlation();

        // Une correlation negative ou nulle veut dire « rien en commun » : c'est le cas
        // fader ferme. On la ramene donc sur zero plutot que de la laisser osciller.
        var target = Clamp01((raw - 0.15f) / 0.70f);

        // Montee plus vive que la descente : quand le DJ ouvre son fader, le visuel doit
        // suivre sans trainer ; quand il le referme parce que le calage ne va pas, mieux
        // vaut que le mur ne reparte pas brutalement en arriere.
        var rate = target > _smoothed ? 0.06f : 0.02f;
        _smoothed += (target - _smoothed) * rate;
        return _smoothed;
    }

    /// <summary>
    /// Remet a zero : nouveau morceau au casque, l'historique precedent n'a plus de sens.
    /// </summary>
    public void Reset()
    {
        _n = 0;
        _smoothed = 0f;
    }

    /// <summary>
    /// Correlation de Pearson entre les deux suites, <b>centree bande par bande</b>.
    ///
    /// Le centrage par bande est le point delicat, et une premiere version le faisait
    /// sur la moyenne globale : la mesure saturait alors a 1 meme fader ferme. La raison
    /// n'etait pas un bug mais une propriete de la musique — deux morceaux quelconques
    /// ont tous les deux plus d'energie dans les graves que dans les aigus. Leurs profils
    /// se ressemblent donc par construction, et cette ressemblance-la n'apprend rien.
    ///
    /// En retranchant la moyenne temporelle propre a chaque bande, on efface la forme
    /// spectrale commune a toute musique et il ne reste que la <b>dynamique</b> : ou ca
    /// monte, ou ca descend, a quel moment. C'est elle qui identifie un morceau dans un
    /// melange, parce qu'elle porte son rythme.
    ///
    /// Pearson et non un produit scalaire : la mesure reste insensible au niveau. Un cue
    /// ecoute fort au casque et le meme morceau discret dans le master donnent la meme
    /// valeur, sinon on mesurerait le volume et non la presence.
    /// </summary>
    private float Correlation()
    {
        double num = 0, da = 0, db = 0;

        for (var b = 0; b < VisualFrame.BandCount; b++)
        {
            double sa = 0, sb = 0;
            for (var i = 0; i < History; i++)
            {
                sa += _master[i, b];
                sb += _cue[i, b];
            }
            var ma = sa / History;
            var mb = sb / History;

            for (var i = 0; i < History; i++)
            {
                var x = _master[i, b] - ma;
                var y = _cue[i, b] - mb;
                num += x * y;
                da += x * x;
                db += y * y;
            }
        }

        if (da < 1e-9 || db < 1e-9) return 0f;
        return (float)(num / Math.Sqrt(da * db));
    }

    private static float Clamp01(float x) => x < 0f ? 0f : x > 1f ? 1f : x;
}
