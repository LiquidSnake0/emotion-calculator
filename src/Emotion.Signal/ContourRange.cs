namespace Emotion.Signal;

/// <summary>
/// Rapporte le contour de chaque source a l'etendue qu'elle occupe reellement.
///
/// LE PROBLEME. Le contour est une hauteur perceptive entre 0 et 1, ou 0 vaut 40 Hz et 1
/// vaut 10 kHz — huit octaves, parce que c'est ce qu'un mix peut contenir. Mais une source
/// donnee n'en parcourt qu'une petite part : mesure sur le repertoire, la troisieme source
/// de Macroblank va de 0,47 a 0,69, soit <b>1,75 octave sur les huit</b>. Sa forme se
/// deplace donc dans un cinquieme de sa case et les quatre autres cinquiemes restent vides
/// en permanence.
///
/// Le DJ l'a dit dans ces termes : « la voix monte vraiment haut et on ne le voit pas ».
/// Le contour etait juste, et illisible.
///
/// CE QU'ON FAIT. Chaque rang apprend ses propres bornes et s'y rapporte : la question
/// posee n'est plus « ou suis-je dans le spectre » mais <b>« ou suis-je dans ce que je
/// parcours »</b>. C'est le meme principe que partout ailleurs ici — une source apprend ce
/// qui est normal pour elle, faute de pouvoir le savoir d'avance.
///
/// CE QU'ON NE FAIT PAS. On n'etire pas une source qui ne bouge pas. Une nappe tenue a
/// hauteur constante ne produit qu'un tremblement de mesure ; le normaliser en ferait un
/// mouvement plein cadre, c'est-a-dire du bruit affiche comme du sens. En dessous de
/// <see cref="EtendueMin"/> — une octave — on rend la hauteur brute et l'on ment pas.
/// Entre les deux, on melange progressivement, pour qu'une source qui s'ouvre au fil du
/// morceau ne saute pas d'un coup.
///
/// LES RANGS, PAS LES SOURCES. On indexe par rang — du grave a l'aigu — et non par numero
/// de source, parce que c'est le rang qui decide de la case a l'ecran. La case 3 montre
/// toujours la troisieme source la plus grave, et c'est son etendue a elle qui compte.
/// </summary>
public sealed class ContourRange
{
    /// <summary>Etendue en dessous de laquelle on ne normalise pas. 0,125 vaut une octave sur huit.</summary>
    public const float EtendueMin = 0.125f;

    /// <summary>Etendue a partir de laquelle on normalise pleinement. Deux octaves.</summary>
    public const float EtendueFranche = 0.25f;

    /// <summary>
    /// Images actives avant que les bornes veuillent dire quelque chose.
    ///
    /// Cinquante images, soit un peu plus d'une seconde de jeu. En dessous, deux
    /// observations suffiraient a definir une etendue, et la premiere note du morceau
    /// deciderait de tout l'affichage.
    /// </summary>
    private const int Assez = 50;

    /// <summary>
    /// Retrecissement des bornes par image active.
    ///
    /// Les bornes s'ouvrent d'un coup et se referment lentement. Sans ce retour, une seule
    /// image aberrante fixerait l'etendue pour tout le morceau ; avec un retour trop vif,
    /// l'etendue suivrait le contour et il n'y aurait plus de reference du tout.
    ///
    /// 0,9993 par image a quarante-sept images par seconde : une borne oubliee met une
    /// trentaine de secondes a se resorber, soit l'ordre de grandeur d'une section.
    /// </summary>
    private const float Retour = 0.9993f;

    private readonly float[] _bas;
    private readonly float[] _haut;
    private readonly int[] _vues;

    public ContourRange(int rangs)
    {
        _bas = new float[rangs];
        _haut = new float[rangs];
        _vues = new int[rangs];
        Oublier();
    }

    /// <summary>Etendue apprise pour ce rang, en fraction des huit octaves. Diagnostic.</summary>
    public float Etendue(int rang) =>
        rang >= 0 && rang < _bas.Length && _vues[rang] > 0
            ? MathF.Max(0f, _haut[rang] - _bas[rang]) : 0f;

    /// <summary>Repart de zero. Le vinyle est range, ce qu'il parcourait ne sert plus.</summary>
    public void Oublier()
    {
        for (var r = 0; r < _bas.Length; r++)
        {
            _bas[r] = float.MaxValue;
            _haut[r] = float.MinValue;
            _vues[r] = 0;
        }
    }

    /// <summary>
    /// Nourrit le rang et rend la position a afficher.
    /// </summary>
    /// <param name="rang">Le rang, du grave a l'aigu. C'est la case a l'ecran.</param>
    /// <param name="hauteur">La hauteur perceptive mesuree, entre 0 et 1.</param>
    /// <param name="actif">
    /// La source joue-t-elle ? Une source muette ne dit rien de son etendue, et l'ecouter
    /// quand meme ferait descendre toutes les bornes vers la valeur au repos.
    /// </param>
    public float Situer(int rang, float hauteur, bool actif)
    {
        if (rang < 0 || rang >= _bas.Length) return hauteur;
        if (!actif) return Rendre(rang, hauteur);

        if (_vues[rang] == 0)
        {
            _bas[rang] = _haut[rang] = hauteur;
        }
        else
        {
            // Les bornes se referment doucement vers ce qu'on voit, puis s'ouvrent d'un
            // coup si la valeur les depasse. L'ordre compte : l'inverse effacerait
            // l'ouverture qu'on vient de faire.
            _bas[rang] += (hauteur - _bas[rang]) * (1f - Retour);
            _haut[rang] += (hauteur - _haut[rang]) * (1f - Retour);
            if (hauteur < _bas[rang]) _bas[rang] = hauteur;
            if (hauteur > _haut[rang]) _haut[rang] = hauteur;
        }

        if (_vues[rang] < int.MaxValue) _vues[rang]++;
        return Rendre(rang, hauteur);
    }

    /// <summary>Les bornes d'un rang, pour l'empreinte.</summary>
    public (float Bas, float Haut, int Vues) Exporter(int rang) =>
        rang >= 0 && rang < _bas.Length ? (_bas[rang], _haut[rang], _vues[rang]) : (float.MaxValue, float.MinValue, 0);

    public void Importer(int rang, float bas, float haut, int vues)
    {
        if (rang < 0 || rang >= _bas.Length) return;
        _bas[rang] = bas; _haut[rang] = haut; _vues[rang] = vues;
    }

    public void Vider(int rang) => Importer(rang, float.MaxValue, float.MinValue, 0);

    /// <summary>Deplace les bornes de chaque rang selon le relais (voir <see cref="MotifSources.Reorganiser"/>).</summary>
    public void Reorganiser(ReadOnlySpan<int> ancienRang)
    {
        var n = _bas.Length;
        var bas = new float[n]; var haut = new float[n]; var vues = new int[n];
        Array.Fill(bas, float.MaxValue); Array.Fill(haut, float.MinValue);
        for (var r = 0; r < n; r++)
        {
            var a = r < ancienRang.Length ? ancienRang[r] : -1;
            if (a < 0 || a >= n) continue;
            bas[r] = _bas[a]; haut[r] = _haut[a]; vues[r] = _vues[a];
        }
        Array.Copy(bas, _bas, n); Array.Copy(haut, _haut, n); Array.Copy(vues, _vues, n);
    }

    private float Rendre(int rang, float hauteur)
    {
        if (_vues[rang] < Assez) return hauteur;

        var etendue = _haut[rang] - _bas[rang];
        if (etendue <= EtendueMin) return hauteur;

        var centre = (_bas[rang] + _haut[rang]) * 0.5f;
        var etire = Clamp01(0.5f + (hauteur - centre) / etendue);

        // Entre une et deux octaves, on passe progressivement du brut a l'etire : une
        // source qui s'ouvre au fil du morceau ne doit pas sauter d'un coup.
        var part = Clamp01((etendue - EtendueMin) / (EtendueFranche - EtendueMin));
        return hauteur + (etire - hauteur) * part;
    }

    private static float Clamp01(float x) => x < 0f ? 0f : x > 1f ? 1f : x;
}
