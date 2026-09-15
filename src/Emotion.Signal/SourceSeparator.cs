namespace Emotion.Signal;

/// <summary>
/// Separe le son en sources, sans savoir lesquelles.
///
/// LE PROBLEME QU'AUCUN FILTRE DE FREQUENCE NE RESOUT. Un piano et un saxophone qui jouent
/// dans la meme octave tombent dans la meme bande : quelle que soit la finesse du
/// decoupage, on additionne leurs deux niveaux et l'on obtient une grandeur qui ne decrit
/// ni l'un ni l'autre. Le DJ l'a dit ainsi : « piano et saxophone qui s'additionnent, ca
/// donne un truc illisible ».
///
/// Deux sons peuvent partager une hauteur ; ils ne partagent pas leur <b>timbre</b>. Et un
/// timbre, ce n'est pas un profil fige : c'est une forme qui <b>glisse avec la note</b>.
/// C'est ce que <see cref="ProfileLearner"/> apprend — un gabarit par source, sur un axe
/// logarithmique en frequence, libre de se placer sur deux octaves.
///
/// DEUX REGIMES, ET C'EST CE QUI LA REND UTILISABLE EN DIRECT.
///
///   apprendre    gabarits et niveaux ensemble, sur quarante secondes. Couteux, fait
///                rarement, et hors du fil d'analyse — c'est le travail du cue.
///   suivre       gabarits figes, niveaux et positions seuls, sur l'image courante.
///                Quelques centaines de milliers d'operations, dans le fil.
///
/// CE QU'ELLE VOIT. Pas le spectre d'analyse a 1024 : a 47 Hz la case, le grave n'a que
/// quatre cases et rien ne peut y glisser. Elle garde ses propres 4096 echantillons, prend
/// sa propre transformee a chaque image et la projette sur 192 cases logarithmiques, vingt-
/// quatre par octave. Le suivi du tempo, des frappes et des registres ne change pas.
/// </summary>
public sealed class SourceSeparator
{
    /// <summary>
    /// Capacite : combien de sources le paquet peut porter. Le nombre publie, lui, est
    /// decouvert par disque (<see cref="Actives"/>).
    ///
    /// HUIT, PARTAGEES ENTRE DEUX DISQUES PENDANT LE FONDU. Le master ne suit pas « la
    /// track », il suit les deux : les gabarits du disque qui sort et ceux du disque qui
    /// entre, cote a cote dans le meme suivi. « Si j'enleve les basses de A, le master saura
    /// que les basses de A ne sont pas la ; on aura le tchak cote A et la basse cote B qui
    /// tournent en meme temps. » Un disque seul n'en apprend jamais plus de
    /// <see cref="MaxAppris"/> ; en fondu, chacun garde au plus <see cref="ParDisqueEnFondu"/>,
    /// et le reste — partage, puisqu'un residu ne se signe pas — prend la derniere case.
    /// </summary>
    public const int Sources = 8;

    /// <summary>
    /// Combien de gabarits un disque seul peut apprendre. Six : au-dela, la stabilite des
    /// gabarits tombe (0,87–0,99 a six, 0,76–0,92 a douze) et la factorisation decoupe des
    /// instruments en morceaux qui ne se retrouvent pas d'un apprentissage a l'autre.
    /// </summary>
    public const int MaxAppris = 6;

    /// <summary>Combien de gabarits chaque disque garde pendant un fondu.</summary>
    public const int ParDisqueEnFondu = 4;

    /// <summary>
    /// SUIVI SEUL : l'analyseur du master ne forme jamais de portrait. Il recoit ceux du cue
    /// et se contente de suivre — c'est la regle « le cue apprend, le master joue », enfin
    /// appliquee jusqu'au bout. Apprendre sur le master reviendrait a apprendre sur une somme
    /// de deux disques, qui n'existe dans aucun des deux.
    /// </summary>
    public bool SuiviSeul { get; set; }

    /// <summary>A quel disque appartient chaque gabarit : 0 celui qui joue, 1 celui qui entre.</summary>
    private readonly int[] _disque = new int[Sources];

    /// <summary>
    /// Le disque de la source de rang donne : 0 le disque qui joue, 1 celui qui entre, 2 le
    /// reste pendant un fondu — un residu porte les deux batteries et ne se signe pas.
    /// </summary>
    public int DisqueOrdonne(int rang)
    {
        if (rang >= 0 && rang < Actives) return _disque[_ordre[rang]];
        if (rang == RangReste) return EnFondu ? 2 : 0;
        return 0;
    }

    /// <summary>Deux portraits suivis a la fois : le fondu est en cours.</summary>
    public bool EnFondu
    {
        get { for (var s = 0; s < Actives; s++) if (_disque[s] != 0) return true; return false; }
    }

    /// <summary>
    /// Combien de temps la separation ecoute pour apprendre. QUARANTE SECONDES, ET C'EST
    /// LE CUE.
    ///
    /// Elle en regardait 2,7 : sur un album entier, cela donnait des tranches de registre et
    /// jamais des instruments. Le DJ a donne la duree qui compte : « un disque reste au
    /// casque dans les 40 secondes ». C'est le temps dont on dispose avant qu'il passe au
    /// master, et c'est donc la fenetre d'apprentissage.
    /// </summary>
    public const float MemoireDefautS = 40f;

    /// <summary>La fenetre de la transformee propre a la separation, en echantillons.</summary>
    public const int FenetreLog = 4096;

    /// <summary>
    /// Une image d'apprentissage sur deux. Un gabarit n'a pas besoin de 21 ms de finesse
    /// pour se definir, et cela divise par deux ce que l'apprentissage coute.
    /// </summary>
    private const int SousEchantillon = 2;

    private readonly int _memoire;

    /// <summary>
    /// Un premier apprentissage rapide, pour que l'ecran ne reste pas noir quarante
    /// secondes. Il cherche un nombre de sources PROVISOIRE, et sera remplace des que la
    /// memoire est pleine par l'apprentissage qui, lui, choisit.
    /// </summary>
    private const int Provisoire = 128;
    private const int SourcesProvisoires = 4;
    private bool _provisoireFait;
    private bool _choixFait;

    /// <summary>
    /// Le verrou : le morceau est su, on ne reapprend plus. « Ne plus chercher a le
    /// retoucher une fois qu'on a capte le boom-tchak. » Pose par l'analyseur quand les
    /// motifs tiennent ; leve par <see cref="Reset"/>, c'est-a-dire par un nouveau disque.
    /// </summary>
    public bool Verrou { get; private set; }
    public void Verrouiller() => Verrou = true;

    /// <summary>
    /// Combien de sources la separation publie EN CE MOMENT — decouvert, pas impose.
    ///
    /// « Des fois on en a 2, des fois 8, c'est justement ce que le programme est cense me
    /// dire. » Zero tant que rien n'a ete appris ; puis le nombre retenu par le balayage.
    /// Les rangs au-dela rendent zero partout.
    /// </summary>
    public int Actives { get; private set; }

    /// <summary>
    /// Combien de cases sont publiees : les sources a gabarit, plus <b>le reste</b>.
    ///
    /// LE RESTE EST UNE CASE, ET C'EST LE KICK. Le kick n'a pas de hauteur qui glisse : trois
    /// bancs ont montre qu'aucun gabarit ne le prend, et le masque repartissait alors son
    /// energie au prorata entre toutes les sources — le DJ entendait « le boom-tchak sur les
    /// trois ». Mesure sur Passepartout, ce que les gabarits n'expliquent pas, mis a part,
    /// correle a 0,77 avec la batterie du juge exterieur (0,55 au mieux avant, colle a la
    /// basse), et la basse s'en nettoie (0,76 → 0,83). Le kick n'est pas une sonorite qui
    /// glisse : c'est ce qui reste quand les sonorites ont parle. Il occupe la derniere case.
    /// </summary>
    public int Publiees => Pret && Actives < Sources ? Actives + 1 : Actives;

    /// <summary>Le rang du reste, ou -1 s'il n'y a pas de place.</summary>
    public int RangReste => Pret && Actives < Sources ? Actives : -1;

    private float _reste;                 // energie non expliquee sur l'image courante
    private float _hauteurReste = 0.5f;   // son centre de gravite, en octaves
    private int _vuesReste;

    /// <summary>Ce que le balayage a mesure, pour la sonde et le diagnostic.</summary>
    public IReadOnlyList<ProfileLearner.Bilan> Bilans => _apprentissage.Bilans;
    public IReadOnlyList<IReadOnlyList<ProfileLearner.Bilan>> Historique => _apprentissage.Historique;
    public bool ChoixFait => _choixFait;

    /// <summary>Iterations de l'apprentissage complet. Au-dela, les gabarits ne bougent plus guere.</summary>
    private const int IterationsApprentissage = 60;

    /// <summary>
    /// Iterations de chaque essai du balayage. A vingt, mesure sur Passepartout, le reste ne
    /// baissait pas de facon monotone d'un K au suivant (1,0 · 0,3 · 0,7 · 0,3 point) : le
    /// hasard de l'amorcage pesait autant que la source ajoutee. A quarante, il se lit.
    /// </summary>
    private const int IterationsBalayage = 40;

    /// <summary>Iterations du suivi, sur la seule image courante.</summary>
    private const int IterationsSuivi = 8;

    /// <summary>
    /// Constante de temps du lissage des niveaux suivis, en secondes.
    ///
    /// SANS LISSAGE, LES GABARITS ABSORBENT LE KICK. Six octaves de large sur quarante-neuf
    /// positions, ils expliquent un coup bref et plat presque aussi bien qu'une note : la
    /// solution d'une image saute pour l'avaler, et le reste ne recoit rien. Mesure sur
    /// Passepartout : le niveau du reste correlait a 0,43 avec la batterie en direct, contre
    /// 0,77 hors ligne — ou les niveaux sont lisses sur huit trames. Un niveau ne peut pas
    /// changer plus vite que la fenetre qui l'estime ; le lissage rend au reste ce qui va plus
    /// vite que 85 ms.
    /// </summary>
    private const float LissageSuiviS = 0.085f;

    private const float Eps = 1e-9f;
    private const int NLog = ProfileLearner.NLog;
    private const int Positions = ProfileLearner.Positions;
    private const int Longueur = ProfileLearner.Longueur;

    private readonly int _rate;
    private readonly int _hop;
    private readonly float[] _w;          // gabarits : Sources x Longueur
    private readonly float[] _v;          // spectrogramme glissant : memoire x NLog
    private readonly float[] _courant;    // niveau de chaque source sur l'image courante
    private readonly float[] _hCourant;   // niveaux par position, lisses dans le temps
    private readonly float[] _hKl;        // la solution brute de l'image, avant lissage
    private readonly float _lissage;      // part de la solution brute prise a chaque image
    private readonly float[] _vh;         // reconstruction de l'image courante
    private readonly float[] _spectre;    // l'image courante, sur l'axe log

    // La transformee propre a la separation.
    private readonly float[] _anneau = new float[FenetreLog];
    private int _anneauEcrit;
    private int _depuisTrame;
    private readonly float[] _hann = Fft.Hann(FenetreLog);
    private readonly float[] _re = new float[FenetreLog];
    private readonly float[] _im = new float[FenetreLog];
    private readonly int[] _fbDebut = new int[NLog];
    private readonly float[][] _fbPoids = new float[NLog][];

    /// <summary>
    /// L'ACCORDAGE DU DISQUE, ESTIME AU CUE. Quatre titres de l'album sont desaccordes d'un
    /// quart de ton (−48 cents : du lo-fi echantillonne sur un vinyle ralenti), et l'axe a
    /// vingt-quatre cases par octave — cinquante cents. Sur ces disques, chaque note tombait
    /// pile entre deux cases. On releve donc, pendant les premieres secondes, ou tombent les
    /// pics du spectre a l'interieur du demi-ton, et l'on decale l'axe d'autant avant
    /// d'apprendre quoi que ce soit. Aucune fiche n'est necessaire : c'est le disque qui le dit.
    /// </summary>
    public float AccordageCents { get; private set; }
    public float F0 => _f0;
    private float _f0 = ProfileLearner.F0;
    private readonly double[] _histoCents = new double[20];   // cinq cents par case, de -50 a +50
    private bool _accordageFait;
    private const float AccordageMin = 15f;                   // en dessous, l'axe est deja bon

    private int _ecrit;
    private int _remplies;
    private int _trame;
    private int _depuisApprentissage;
    private readonly Random _alea = new(1203);

    /// <summary>Niveau de chaque source sur l'image courante.</summary>
    public IReadOnlyList<float> Activations => _courant;

    /// <summary>A-t-on appris des gabarits, ou rend-on encore du bruit ?</summary>
    public bool Pret { get; private set; }

    /// <summary>Longueur d'un gabarit, en cases logarithmiques.</summary>
    public int Bins => Longueur;

    /// <summary>
    /// Le gabarit d'une source, dans l'ordre du grave a l'aigu. Lecture seule.
    ///
    /// POURQUOI IL SORT D'ICI. Toutes les mesures de ce projet disent si une source est
    /// REGULIERE ; aucune ne dit si elle contient ce qu'elle pretend contenir. Un gabarit
    /// exporte permet de reconstruire ce que la source a retenu, en son, et de l'ECOUTER —
    /// ce que l'oreille tranche en dix secondes et qu'aucun chiffre n'a su dire.
    /// </summary>
    public void ProfilOrdonne(int rang, Span<float> sortie)
    {
        if ((uint)rang >= Actives || sortie.Length < Longueur) return;
        _w.AsSpan(_ordre[rang] * Longueur, Longueur).CopyTo(sortie);
    }

    /// <summary>
    /// Hauteur habituelle de chaque source, sur une echelle d'octaves entre 0 et 1 : la
    /// couleur du gabarit plus la position ou il a joue en moyenne. Sert a ordonner les
    /// sources du grave a l'aigu, faute de savoir les nommer.
    /// </summary>
    public IReadOnlyList<float> Hauteurs => _hauteurs;

    private readonly float[] _hauteurs = new float[Sources];
    private readonly float[] _centres = new float[Sources];         // couleur du gabarit, en cases

    /// <summary>
    /// LA GAMME DE LA FICHE, POUR LES DEGRES. Une preference douce sur les positions a ete
    /// essayee (0,7 hors gamme) : juge Demucs et motifs identiques a la decimale sur cinq
    /// titres — retiree, une position reste libre. Ce que la gamme rapporte, c'est le degre.
    /// </summary>
    private string? _camelot;
    private int _caseResolue;   // la premiere case de l'axe ou une raie fait moins d'un demi-ton
    public void Gamme(string? camelot) => _camelot = Emotion.Signal.Gamme.Classes(camelot) is null ? null : camelot;
    public string? Camelot => _camelot;

    /// <summary>
    /// La classe de hauteur d'un gabarit : son chromagramme replie — chaque case va a sa
    /// classe, les octaves se retrouvent, le fondamental et ses octaves l'emportent. Lire le
    /// fondamental directement echouait dans le grave : a 55 Hz une raie de 11,7 Hz fait plus
    /// de trois demi-tons, et la premiere bosse du gabarit tombait a cote.
    /// </summary>
    /// <summary>
    /// La classe de hauteur d'un spectre sur l'axe log, replie en chromagramme — et
    /// SEULEMENT LA OU L'AXE RESOUT LE DEMI-TON. Dans le grave, une raie de la transformee
    /// couvre plusieurs cases : la projection y etale un la1 jusqu'au la#, et le repli votait
    /// pour la mauvaise classe. On ne compte que les cases dont la raie fait moins d'un demi-
    /// ton — au-dessus de 200 Hz a 48 kHz — ou les octaves du fondamental votent juste.
    /// </summary>
    private int ClasseDeReconstruction(ReadOnlySpan<float> spectreLog)
    {
        Span<float> chroma = stackalloc float[12];
        for (var c = _caseResolue; c < spectreLog.Length; c++) chroma[(int)MathF.Round(c / 2f) % 12] += spectreLog[c];
        var meilleur = 0; var second = 0f;
        for (var k = 1; k < 12; k++) if (chroma[k] > chroma[meilleur]) meilleur = k;
        for (var k = 0; k < 12; k++) if (k != meilleur && chroma[k] > second) second = chroma[k];
        // UN VOTE SERRE N'EST PAS UNE NOTE. Une basse dont seules les harmoniques resolues
        // votent, ou une nappe qui etale son energie, donnent deux classes a egalite : on
        // rend « inconnu » plutot qu'un degre faux — mesure, la basse fabriquee tombait un
        // demi-ton a cote quand on la forcait.
        return chroma[meilleur] >= MargeClasse * second ? meilleur : -1;
    }

    /// <summary>La classe gagnante doit peser au moins ce facteur de plus que la suivante.</summary>
    public const float MargeClasse = 1.3f;

    private readonly int[] _classeCourante = new int[Sources];

    /// <summary>
    /// LA PART DOMINANTE D'UNE SOURCE : la fraction de son energie qui vit dans des cases
    /// dont elle est proprietaire (plus de la moitie), contre la part discrete, dans des
    /// cases partagees ou elle n'est que minoritaire — ce qu'on entend « en cachette »
    /// derriere une piste. « Pourquoi pas afficher dans une autre couleur la partie
    /// discrete ? » Couper, c'est decider a la place de l'oeil ; colorer, c'est lui dire
    /// ce qu'on sait et ce qu'on ne sait pas. Mesure : la basse et le kick partagent leurs
    /// raies graves a parts presque egales, une porte n'y change rien (fuite 0,43 → 0,45)
    /// et le binaire tue la basse (0,78 → 0,34). On publie donc la dominance, on ne coupe pas.
    /// </summary>
    private readonly float[] _dominance = new float[Sources];
    private readonly float[][] _recParSource = Enumerable.Range(0, Sources).Select(_ => new float[NLog]).ToArray();
    private float _dominanceReste;

    public float DominanceOrdonnee(int rang) =>
        rang >= 0 && rang < Actives ? _dominance[_ordre[rang]]
        : rang == RangReste ? _dominanceReste
        : 0f;

    /// <summary>La classe de hauteur (do = 0) que joue la source de rang donne, ou -1. Diagnostic.</summary>
    public int ClasseOrdonnee(int rang) => rang >= 0 && rang < Actives ? _classeCourante[_ordre[rang]] : -1;

    /// <summary>La position courante (en cases) et le centre du gabarit, pour le diagnostic.</summary>
    public (float Position, float Centre) PositionOrdonnee(int rang) =>
        rang >= 0 && rang < Actives ? (_positionCourante[_ordre[rang]], _centres[_ordre[rang]]) : (0f, 0f);

    /// <summary>
    /// Le degre que joue la source de rang donne, en ce moment : 0 la tonique … 6, 7 hors
    /// gamme, 15 sans gamme ou sans note. Le reste n'a pas de degre.
    /// </summary>
    public int DegreOrdonne(int rang)
    {
        if (_camelot is null || rang < 0 || rang >= Actives) return Emotion.Signal.Gamme.Inconnu;
        var s = _ordre[rang];
        if (_courant[s] <= Eps) return Emotion.Signal.Gamme.Inconnu;
        var classe = _classeCourante[s];
        return classe < 0 ? Emotion.Signal.Gamme.Inconnu : Emotion.Signal.Gamme.Degre(_camelot, classe);
    }
    private readonly float[] _positionsApprises = new float[Sources];
    private readonly float[] _positionCourante = new float[Sources];
    private readonly int[] _ordre = new int[Sources];

    /// <summary>
    /// A quel point le gabarit de chaque source tient d'un apprentissage a l'autre, 0 a 1.
    /// Une source dont le gabarit se retrouve identique est un objet stable du morceau ; un
    /// gabarit qui change a chaque fois melange plusieurs choses.
    /// </summary>
    private readonly float[] _stabilite = new float[Sources];
    private readonly float[] _profilPrecedent;
    private bool _profilConnu;

    /// <summary>
    /// Stabilite du gabarit de la source de rang donne, du grave a l'aigu. Le reste n'a pas
    /// de gabarit qui pourrait bouger : il est ce qu'il est, toujours le meme objet.
    /// </summary>
    public float StabiliteOrdonnee(int rang) =>
        rang >= 0 && rang < Actives ? _stabilite[_ordre[rang]]
        : rang == RangReste ? 1f
        : 0f;

    /// <summary>
    /// Combien d'images cette source a passe a jouer. C'est le pendant de la stabilite :
    /// l'une dit si la source est un objet net, l'autre si on l'a assez vue pour en juger.
    /// </summary>
    private const int Assez = 200;
    private readonly int[] _vues = new int[Sources];

    /// <summary>Sous ce niveau relatif, la source ne joue pas et n'apprend rien d'elle.</summary>
    private const float Audible = 0.12f;

    public float EcouteOrdonnee(int rang) =>
        rang >= 0 && rang < Actives ? MathF.Min(1f, _vues[_ordre[rang]] / (float)Assez)
        : rang == RangReste ? MathF.Min(1f, _vuesReste / (float)Assez)
        : 0f;

    /// <param name="hop">Echantillons entre deux images d'analyse.</param>
    /// <param name="memoire">Images d'analyse gardees pour apprendre ; zero = quarante secondes.</param>
    public SourceSeparator(int sampleRate = 48_000, int hop = 1024, int memoire = 0)
    {
        _rate = sampleRate;
        _hop = hop;
        var images = memoire > 0 ? memoire : (int)(MemoireDefautS * sampleRate / hop);
        _memoire = Math.Max(Provisoire, images / SousEchantillon);
        _w = new float[Sources * Longueur];
        _v = new float[_memoire * NLog];
        _courant = new float[Sources];
        _hCourant = new float[Sources * Positions];
        _hKl = new float[Sources * Positions];
        _lissage = 1f - MathF.Exp(-hop / (LissageSuiviS * sampleRate));
        _vh = new float[NLog];
        _spectre = new float[NLog];
        _profilPrecedent = new float[Sources * Longueur];

        for (var i = 0; i < _w.Length; i++) _w[i] = 0.1f + (float)_alea.NextDouble() * 0.9f;
        for (var s = 0; s < Sources; s++) _ordre[s] = s;
        var raieHz = sampleRate / (float)FenetreLog;
        var hzResolu = raieHz / (MathF.Pow(2f, 1f / 12f) - 1f);
        _caseResolue = Math.Clamp((int)MathF.Ceiling(ProfileLearner.ParOctave * MathF.Log2(hzResolu / ProfileLearner.F0)), 0, Longueur - 1);
        ConstruireProjection();

        _apprentissage = new ProfileLearner(Sources, _memoire, IterationsApprentissage);
    }

    private readonly ProfileLearner _apprentissage;

    /// <summary>
    /// La projection des raies lineaires sur les cases logarithmiques : un triangle par case,
    /// large d'une case ou d'une raie, la plus grande des deux. Dans le grave, ou les raies
    /// sont plus espacees que les cases, chaque case interpole entre ses deux raies ; dans
    /// l'aigu, ou elles sont plus serrees, chaque case en moyenne plusieurs.
    /// </summary>
    private void ConstruireProjection(float cents = 0f)
    {
        _f0 = ProfileLearner.F0 * MathF.Pow(2f, cents / 1200f);
        AccordageCents = cents;
        var raieHz = _rate / (float)FenetreLog;
        var raies = FenetreLog / 2 + 1;
        var ratio = MathF.Pow(2f, 1f / ProfileLearner.ParOctave) - 1f;
        for (var l = 0; l < NLog; l++)
        {
            var fc = _f0 * MathF.Pow(2f, l / (float)ProfileLearner.ParOctave);
            var demi = MathF.Max(fc * ratio, raieHz);
            var debut = Math.Max(0, (int)MathF.Ceiling((fc - demi) / raieHz));
            var fin = Math.Min(raies - 1, (int)MathF.Floor((fc + demi) / raieHz));
            var poids = new float[Math.Max(0, fin - debut + 1)];
            float somme = 0;
            for (var b = debut; b <= fin; b++)
            {
                var p = 1f - MathF.Abs(b * raieHz - fc) / demi;
                if (p < 0f) p = 0f;
                poids[b - debut] = p;
                somme += p;
            }
            if (somme > Eps) for (var i = 0; i < poids.Length; i++) poids[i] /= somme;
            _fbDebut[l] = debut;
            _fbPoids[l] = poids;
        }
    }

    /// <summary>
    /// Des echantillons, dans l'ordre. A chaque <c>hop</c> echantillons, une image est prise
    /// sur les 4096 derniers, projetee sur l'axe logarithmique, et suivie.
    /// </summary>
    public void Feed(ReadOnlySpan<float> samples)
    {
        foreach (var x in samples)
        {
            _anneau[_anneauEcrit] = x;
            _anneauEcrit = (_anneauEcrit + 1) % FenetreLog;
            if (++_depuisTrame >= _hop)
            {
                _depuisTrame = 0;
                Trame();
            }
        }
    }

    private void Trame()
    {
        // La transformee des 4096 derniers echantillons, fenetres.
        for (var i = 0; i < FenetreLog; i++)
        {
            _re[i] = _anneau[(_anneauEcrit + i) % FenetreLog] * _hann[i];
            _im[i] = 0f;
        }
        Fft.Forward(_re, _im);
        var raies = FenetreLog / 2 + 1;
        // La magnitude, reutilisee en place dans _re.
        for (var b = 0; b < raies; b++) _re[b] = MathF.Sqrt(_re[b] * _re[b] + _im[b] * _im[b]);
        if (!_accordageFait) RelevierAccordage(raies);
        for (var l = 0; l < NLog; l++)
        {
            var poids = _fbPoids[l];
            var debut = _fbDebut[l];
            float somme = 0;
            for (var i = 0; i < poids.Length; i++) somme += poids[i] * _re[debut + i];
            _spectre[l] = somme;
        }

        if (_trame++ % SousEchantillon == 0) Memoriser();
        Suivre();

        // Une source ne compte comme vue que quand elle joue. Le maximum sert de reference :
        // une source discrete mais presente doit compter, une source a zero non.
        var fort = 1e-4f;
        for (var i = 0; i < Actives; i++) fort = MathF.Max(fort, _courant[i]);
        fort = MathF.Max(fort, _reste);
        for (var i = 0; i < Actives; i++)
            if (_courant[i] / fort > Audible && _vues[i] < Assez) _vues[i]++;
        if (_reste / fort > Audible && _vuesReste < Assez) _vuesReste++;
    }

    /// <summary>
    /// Les pics du spectre entre 80 et 2000 Hz, et ou ils tombent dans le demi-ton : un
    /// histogramme pondere par leur niveau, dont le maximum est l'accordage du disque.
    /// </summary>
    private void RelevierAccordage(int raies)
    {
        var raieHz = _rate / (float)FenetreLog;
        var debut = Math.Max(2, (int)(80f / raieHz));
        var fin = Math.Min(raies - 2, (int)(2000f / raieHz));
        for (var b = debut; b <= fin; b++)
        {
            var m = _re[b];
            if (m <= _re[b - 1] || m <= _re[b + 1] || m < 1e-4f) continue;
            // LE PIC S'INTERPOLE, SINON L'ESTIMATION EST DU BRUIT. Une raie fait 11,7 Hz : a
            // 440 Hz c'est quarante-six cents, presque une case entiere. Une parabole sur les
            // trois raies autour du pic rend sa position a un ou deux cents pres.
            var g = MathF.Log(_re[b - 1] + 1e-9f); var c0 = MathF.Log(m + 1e-9f); var d = MathF.Log(_re[b + 1] + 1e-9f);
            var denom = g - 2f * c0 + d;
            var delta = MathF.Abs(denom) > 1e-9f ? Math.Clamp(0.5f * (g - d) / denom, -0.5f, 0.5f) : 0f;
            var cents = 1200.0 * Math.Log2((b + delta) * raieHz / 440.0);
            var reste = ((cents % 100) + 150) % 100 - 50;              // de -50 a +50
            var k = Math.Clamp((int)((reste + 50) / 5), 0, 19);
            _histoCents[k] += m;
        }
    }

    /// <summary>
    /// A la fin du releve, l'axe se decale de l'accordage trouve, et la memoire repart :
    /// ce qu'elle contenait etait projete sur un axe qui n'etait pas celui du disque.
    /// </summary>
    private void Accorder()
    {
        _accordageFait = true;
        var meilleur = 0; double total = 0;
        for (var k = 0; k < 20; k++) { total += _histoCents[k]; if (_histoCents[k] > _histoCents[meilleur]) meilleur = k; }
        if (total <= 0) return;
        var cents = (meilleur + 0.5f) * 5f - 50f;
        if (MathF.Abs(cents) < AccordageMin) return;
        ConstruireProjection(cents);
        Array.Clear(_v);
        _remplies = _ecrit = 0;
    }

    /// <summary>Range l'image dans la memoire, et lance ce qui doit l'etre.</summary>
    private void Memoriser()
    {
        _spectre.AsSpan().CopyTo(_v.AsSpan(_ecrit * NLog, NLog));
        _ecrit = (_ecrit + 1) % _memoire;
        if (_remplies < _memoire) _remplies++;

        // Le master ne forme aucun portrait : il ne fait que suivre ce que le cue lui a
        // transmis. Sans cette garde, le provisoire partirait sur le melange des deux disques.
        if (SuiviSeul) return;

        // D'ABORD UN PROVISOIRE, VITE ; PUIS LE VRAI, QUAND ON A ENTENDU ASSEZ.
        //
        // Les quarante secondes sont le prix d'un apprentissage qui distingue des
        // instruments. Mais quarante secondes d'ecran noir a chaque disque ne se defendent
        // pas : on apprend donc une premiere fois tot, a un nombre de sources provisoire, et
        // l'on remplace tout des que la memoire est pleine.
        var provisoire = Math.Min(Provisoire, _memoire / 2);
        if (!_accordageFait && _remplies >= provisoire)
        {
            // L'accordage se decide sur les premieres secondes, avant tout apprentissage.
            Accorder();
            if (_remplies < provisoire) return;
        }
        if (!_provisoireFait && _remplies >= provisoire)
        {
            if (_apprentissage.TryStart(_v, _w, SourcesProvisoires, provisoire))
                _provisoireFait = true;
        }
        else if (!Verrou && _remplies >= _memoire && (!_choixFait || ++_depuisApprentissage >= _memoire / 2))
        {
            // Le CHOIX du nombre de sources se fait une fois par disque, DES QUE la memoire
            // est pleine : il attendait encore une demi-memoire de plus, et les pistes en
            // solo n'arrivaient qu'a deux minutes vingt — le DJ fermait la fenetre avant,
            // deux fois de suite. Ensuite on reapprend au nombre retenu, une fois par
            // demi-memoire, en repartant des gabarits courants : ils restent a leur place.
            // SOUS LE VERROU, rien de tout cela ne tourne plus : le morceau est su. Figer les
            // gabarits en laissant la croissance continuer a ete essaye — sans adaptation,
            // « du neuf » apparaissait a chaque essai et Glyph Chamber montait a cinq
            // sources. Le verrou est strict, et il attend seize mesures de motif tenu pour
            // laisser aux instruments qui entrent le temps d'avoir leur case.
            var lance = _choixFait
                ? _apprentissage.TryStartCroissance(_v, _w, Actives, _memoire)
                : _apprentissage.TryStartChoix(_v, 2, MaxAppris, _memoire, IterationsBalayage);
            if (lance) _depuisApprentissage = 0;
        }

        // Les gabarits fraichement appris sont repris ici, entre deux images, sur le fil
        // d'analyse : c'est le seul instant ou ils changent.
        if (_apprentissage.TryAdopt(_w, _positionsApprises))
        {
            var avant = Actives;
            Actives = _apprentissage.DernierK;
            MesurerHauteurs();
            if (_apprentissage.DernierEtaitChoix)
            {
                // Le choix range les sources du grave a l'aigu, une fois par disque. Un
                // nombre de sources qui change rend la stabilite par rang sans objet.
                _choixFait = true;
                if (Actives != avant) _profilConnu = false;
                Ordonner();
            }
            else
            {
                // LA CROISSANCE NE REORDONNE PAS. Une source qui entre prend la case
                // suivante : la case 2 reste la case 2, ce que le DJ y a entendu y reste.
                for (var r = avant; r < Actives; r++) _ordre[r] = r;
            }
            MesurerStabilite();
            Pret = true;
        }
    }

    /// <summary>Fait apprendre sur le fil d'analyse. Pour la mesure et les tests.</summary>
    public bool ApprentissageEnLigne
    {
        get => _apprentissage.RunInline;
        set => _apprentissage.RunInline = value;
    }

    /// <summary>Ce que l'apprentissage a coute, pour la sonde.</summary>
    public double ApprentissageMs => _apprentissage.AverageMs;
    public double ApprentissagePireMs => _apprentissage.WorstMs;
    public int Apprentissages => _apprentissage.Runs;

    public void Reset()
    {
        Array.Clear(_v);
        Array.Clear(_vues);
        Array.Clear(_anneau);
        _profilConnu = false;
        Array.Clear(_stabilite);
        _remplies = _ecrit = _depuisApprentissage = _trame = 0;
        Pret = false;
        Actives = 0;
        _provisoireFait = false;
        _choixFait = false;
        Verrou = false;
        _accordageFait = false;
        Array.Clear(_histoCents);
        ConstruireProjection(0f);
        Array.Clear(_courant);
        Array.Clear(_hCourant);
        Array.Clear(_hKl);
        _reste = 0f;
        _hauteurReste = 0.5f;
        _vuesReste = 0;
        Array.Clear(_disque);
        for (var i = 0; i < _w.Length; i++) _w[i] = 0.1f + (float)_alea.NextDouble() * 0.9f;
    }

    // ------------------------------------------------------------------ le relais

    /// <summary>
    /// L'EMPREINTE D'UN DISQUE : ce que le cue a appris et que le master recevra tel quel.
    /// Les gabarits dans l'ordre des rangs (du grave a l'aigu), la position ou chacun a joue,
    /// sa couleur, sa stabilite, combien on l'a entendu, et l'accordage de l'axe sur lequel
    /// tout cela a ete mesure. Aucun niveau instantane : c'est un portrait, pas une image.
    /// </summary>
    public sealed record Empreinte(
        int Actives, float[] Gabarits, float[] Positions, float[] Hauteurs, float[] Centres,
        float[] Stabilite, int[] Vues, float AccordageCents, string? Camelot)
    {
        public static readonly Empreinte Vide = new(0, [], [], [], [], [], [], 0f, null);
    }

    /// <summary>Le portrait courant, dans l'ordre des rangs publies.</summary>
    public Empreinte Portrait()
    {
        var k = Actives;
        var e = new Empreinte(k, new float[k * Longueur], new float[k], new float[k], new float[k],
                              new float[k], new int[k], AccordageCents, _camelot);
        for (var r = 0; r < k; r++)
        {
            var s = _ordre[r];
            _w.AsSpan(s * Longueur, Longueur).CopyTo(e.Gabarits.AsSpan(r * Longueur, Longueur));
            e.Positions[r] = _positionsApprises[s];
            e.Hauteurs[r] = _hauteurs[s];
            e.Centres[r] = _centres[s];
            e.Stabilite[r] = _stabilite[s];
            e.Vues[r] = _vues[s];
        }
        return e;
    }

    /// <summary>
    /// Ce que le relais a fait des rangs : pour chaque nouveau rang, l'ancien rang du disque
    /// qui jouait (ou -1), et le rang dans l'empreinte entrante (ou -1). L'analyseur s'en
    /// sert pour deplacer ce qu'il tient par rang — motifs, caractere, crete, etendue.
    /// </summary>
    public readonly record struct Relais(int[] AncienRang, int[] RangEntrant, int Actives);

    /// <summary>
    /// ADOPTE un portrait comme seul disque : le master qui ne suivait rien recoit le disque
    /// du cue. Tout ce qui s'apprend est fige ; la projection prend l'accordage du disque.
    /// </summary>
    public Relais AdopterPortrait(Empreinte e, int disque = 0)
    {
        var k = Math.Min(e.Actives, Sources - 1);
        ConstruireProjection(e.AccordageCents);
        _accordageFait = true;
        Array.Clear(_v); _remplies = _ecrit = 0;
        Array.Clear(_hCourant); Array.Clear(_hKl); Array.Clear(_courant);
        for (var s = 0; s < Sources; s++) { _ordre[s] = s; _disque[s] = 0; _vues[s] = 0; _stabilite[s] = 0f; }
        for (var r = 0; r < k; r++) PoserGabarit(r, e, r, disque, 0f);
        Actives = k;
        _camelot = e.Camelot;
        _provisoireFait = _choixFait = true;
        Pret = k > 0;
        Verrou = true;
        _profilConnu = false;
        _reste = 0f; _vuesReste = 0;
        var rel = new Relais(new int[Sources], new int[Sources], k);
        Array.Fill(rel.AncienRang, -1); Array.Fill(rel.RangEntrant, -1);
        for (var r = 0; r < k; r++) rel.RangEntrant[r] = r;
        return rel;
    }

    /// <summary>
    /// ACCUEILLE le disque qui entre, a cote de celui qui joue. Chacun garde au plus
    /// <see cref="ParDisqueEnFondu"/> gabarits — les plus entendus — et le suivi porte
    /// desormais les deux jeux dans une seule passe : sur chaque image, ce qui est a A et
    /// ce qui est a B. Les positions apprises de B sont ramenees sur l'axe courant : un
    /// gabarit glisse, il ne depend pas de l'accordage ; seule la place ou il a joue en depend.
    /// </summary>
    public Relais AccueillirPortrait(Empreinte e)
    {
        // Rien ne jouait encore : le disque qui entre est adopte tel quel, mais marque
        // « entrant », pour que la fin du fondu le retague comme les autres fois.
        if (!Pret || Actives == 0) return AdopterPortrait(e, 1);
        var kB = Math.Min(e.Actives, ParDisqueEnFondu);
        var kA = Math.Min(Actives, Sources - 1 - kB);
        // Les rangs de A a garder : les plus entendus, dans leur ordre.
        var garder = new bool[Actives];
        var rangsA = Enumerable.Range(0, Actives).OrderByDescending(r => _vues[_ordre[r]]).ThenBy(r => r).Take(kA);
        foreach (var r in rangsA) garder[r] = true;
        var rel = Compacter(garder);
        // Les rangs de B a prendre : les plus entendus, dans leur ordre.
        var rangsB = Enumerable.Range(0, e.Actives).OrderByDescending(r => e.Vues[r]).ThenBy(r => r).Take(kB).OrderBy(r => r).ToArray();
        var decalage = (e.AccordageCents - AccordageCents) / (1200f / ProfileLearner.ParOctave);
        var k = rel.Actives;
        foreach (var rB in rangsB)
        {
            PoserGabarit(k, e, rB, 1, decalage);
            rel.RangEntrant[k] = rB;
            k++;
        }
        Actives = k;
        Verrou = true;
        _profilConnu = false;
        return rel with { Actives = k };
    }

    /// <summary>
    /// RETIRE un disque du suivi : le fondu est fini, celui qui sortait s'est tu. Les gabarits
    /// de l'autre restent a leur place, et deviennent le disque qui joue.
    /// </summary>
    public Relais RetirerDisque(int disque)
    {
        var garder = new bool[Actives];
        for (var r = 0; r < Actives; r++) garder[r] = _disque[_ordre[r]] != disque;
        var rel = Compacter(garder);
        for (var s = 0; s < Sources; s++) _disque[s] = 0;
        Pret = Actives > 0;
        return rel;
    }

    /// <summary>Copie le gabarit de rang <paramref name="rE"/> de l'empreinte dans la ligne <paramref name="s"/>.</summary>
    private void PoserGabarit(int s, Empreinte e, int rE, int disque, float decalagePositions)
    {
        e.Gabarits.AsSpan(rE * Longueur, Longueur).CopyTo(_w.AsSpan(s * Longueur, Longueur));
        _positionsApprises[s] = Math.Clamp(e.Positions[rE] + decalagePositions, 0f, Positions - 1);
        _positionCourante[s] = _positionsApprises[s];
        _centres[s] = e.Centres[rE];
        _hauteurs[s] = EnOctavesHz(CaseEnHz(_centres[s] + _positionsApprises[s]));
        _stabilite[s] = e.Stabilite[rE];
        _vues[s] = e.Vues[rE];
        _disque[s] = disque;
        _classeCourante[s] = -1;
        _dominance[s] = 0f;
        Array.Clear(_hCourant, s * Positions, Positions);
        Array.Clear(_hKl, s * Positions, Positions);
        _courant[s] = 0f;
    }

    /// <summary>
    /// Ne garde que les rangs marques, dans leur ordre, et les ramene en tete des lignes.
    /// Apres quoi l'ordre est l'identite : le rang r vit dans la ligne r.
    /// </summary>
    private Relais Compacter(bool[] garder)
    {
        var rel = new Relais(new int[Sources], new int[Sources], 0);
        Array.Fill(rel.AncienRang, -1); Array.Fill(rel.RangEntrant, -1);
        var nouveau = 0;
        // On passe par une copie des lignes a garder : une ligne peut ecraser une ligne qui
        // n'a pas encore ete lue si l'on deplace en place.
        var w = new float[Sources * Longueur]; var h = new float[Sources * Positions];
        var pos = new float[Sources]; var posC = new float[Sources]; var cen = new float[Sources];
        var hau = new float[Sources]; var sta = new float[Sources]; var vu = new int[Sources];
        var dq = new int[Sources]; var cl = new int[Sources]; var cour = new float[Sources];
        for (var r = 0; r < garder.Length; r++)
        {
            if (!garder[r]) continue;
            var s = _ordre[r];
            _w.AsSpan(s * Longueur, Longueur).CopyTo(w.AsSpan(nouveau * Longueur, Longueur));
            _hCourant.AsSpan(s * Positions, Positions).CopyTo(h.AsSpan(nouveau * Positions, Positions));
            pos[nouveau] = _positionsApprises[s]; posC[nouveau] = _positionCourante[s];
            cen[nouveau] = _centres[s]; hau[nouveau] = _hauteurs[s]; sta[nouveau] = _stabilite[s];
            vu[nouveau] = _vues[s]; dq[nouveau] = _disque[s]; cl[nouveau] = _classeCourante[s]; cour[nouveau] = _courant[s];
            rel.AncienRang[nouveau] = r;
            nouveau++;
        }
        Array.Copy(w, _w, w.Length); Array.Copy(h, _hCourant, h.Length); Array.Copy(h, _hKl, h.Length);
        Array.Copy(pos, _positionsApprises, Sources); Array.Copy(posC, _positionCourante, Sources);
        Array.Copy(cen, _centres, Sources); Array.Copy(hau, _hauteurs, Sources); Array.Copy(sta, _stabilite, Sources);
        Array.Copy(vu, _vues, Sources); Array.Copy(dq, _disque, Sources); Array.Copy(cl, _classeCourante, Sources);
        Array.Copy(cour, _courant, Sources);
        for (var s = nouveau; s < Sources; s++)
        {
            for (var i = 0; i < Longueur; i++) _w[s * Longueur + i] = 0.1f + (float)_alea.NextDouble() * 0.9f;
            _vues[s] = 0; _stabilite[s] = 0f; _disque[s] = 0; _courant[s] = 0f;
        }
        Array.Clear(_dominance);
        for (var s = 0; s < Sources; s++) _ordre[s] = s;
        Actives = nouveau;
        return rel with { Actives = nouveau };
    }

    /// <summary>
    /// Suit l'image courante, gabarits figes : pour chaque source, a quelle position et a
    /// quel niveau elle joue. La regle de Kullback-Leibler, sur une seule colonne.
    /// </summary>
    private void Suivre()
    {
        if (Actives <= 0) return;
        // On repart des niveaux de l'image d'avant, planches a un minimum : une position qui
        // etait a zero doit pouvoir revenir quand la note revient.
        var n = Actives * Positions;
        for (var i = 0; i < n; i++) _hKl[i] = MathF.Max(1e-4f, _hCourant[i]);
        Array.Clear(_hCourant, n, (Sources - Actives) * Positions);

        for (var it = 0; it < IterationsSuivi; it++)
        {
            _vh.AsSpan().Fill(Eps);
            for (var s = 0; s < Actives; s++)
            {
                var w = _w.AsSpan(s * Longueur, Longueur);
                for (var p = 0; p < Positions; p++)
                    ProfileLearner.Ajouter(_vh.AsSpan(p, Longueur), w, _hKl[s * Positions + p]);
            }
            for (var f = 0; f < NLog; f++) _vh[f] = (_spectre[f] + Eps) / _vh[f];
            for (var s = 0; s < Actives; s++)
            {
                var w = _w.AsSpan(s * Longueur, Longueur);
                for (var p = 0; p < Positions; p++)
                    _hKl[s * Positions + p] *= ProfileLearner.Produit(w, _vh.AsSpan(p, Longueur));
            }
        }
        // Le lissage : les niveaux publies suivent la solution brute avec la constante de
        // temps de la fenetre. Ce que la solution brute a de plus rapide va au reste.
        for (var i = 0; i < n; i++) _hCourant[i] += (_hKl[i] - _hCourant[i]) * _lissage;

        for (var s = 0; s < Sources; s++)
        {
            if (s >= Actives) { _courant[s] = 0f; continue; }
            double niveau = 0, poids = 0;
            for (var p = 0; p < Positions; p++)
            {
                var h = _hCourant[s * Positions + p];
                niveau += h;
                poids += h * p;
            }
            _courant[s] = (float)niveau;
            if (niveau > Eps) _positionCourante[s] = (float)(poids / niveau);
        }

        // LE RESTE : ce que les gabarits n'expliquent pas sur cette image, et ou il vit. Une
        // reconstruction de plus, sans le rapport, puis la difference positive case par case.
        // Au passage, la reconstruction de CHAQUE source, dont on lit la classe de hauteur
        // qu'elle joue : le gabarit a toutes ses positions, pese par ses niveaux — pas une
        // position moyenne arrondie, qui glissait d'un demi-ton.
        _vh.AsSpan().Fill(Eps);
        for (var s = 0; s < Actives; s++)
        {
            var w = _w.AsSpan(s * Longueur, Longueur);
            var rec = _recParSource[s].AsSpan();
            rec.Clear();
            for (var p = 0; p < Positions; p++)
                ProfileLearner.Ajouter(rec.Slice(p, Longueur), w, _hCourant[s * Positions + p]);
            _classeCourante[s] = ClasseDeReconstruction(rec);
            for (var f = 0; f < NLog; f++) _vh[f] += rec[f];
        }
        double reste = 0, centre = 0, resteDominant = 0;
        Span<double> dominant = stackalloc double[Sources];
        Span<double> energie = stackalloc double[Sources];
        for (var f = 0; f < NLog; f++)
        {
            var d = _spectre[f] - _vh[f];
            var r = d > 0f ? d : 0f;
            var total = _vh[f] + r;
            if (d > 0f) { reste += d; centre += d * f; if (r >= 0.5f * total) resteDominant += r; }
            for (var s = 0; s < Actives; s++)
            {
                var v = _recParSource[s][f];
                energie[s] += v;
                if (v >= 0.5f * total) dominant[s] += v;
            }
        }
        for (var s = 0; s < Sources; s++)
            _dominance[s] = s < Actives && energie[s] > Eps ? (float)(dominant[s] / energie[s]) : 0f;
        _dominanceReste = reste > Eps ? (float)(resteDominant / reste) : 0f;
        _reste = (float)reste;
        if (reste > Eps) _hauteurReste = EnOctavesHz(CaseEnHz((float)(centre / reste)));
    }

    /// <summary>
    /// Range les sources du grave a l'aigu, par la couleur de leur gabarit et la position
    /// ou il a joue. Sans nom, il faut au moins un ordre stable : sans lui, la source
    /// affichee en premiere case changerait a chaque apprentissage.
    /// </summary>
    private void MesurerHauteurs()
    {
        for (var s = 0; s < Sources; s++)
        {
            double poids = 0, total = 0;
            var w = _w.AsSpan(s * Longueur, Longueur);
            for (var f = 0; f < Longueur; f++)
            {
                poids += w[f] * f;
                total += w[f];
            }
            _centres[s] = total > Eps ? (float)(poids / total) : Longueur / 2f;
            _hauteurs[s] = EnOctavesHz(CaseEnHz(_centres[s] + _positionsApprises[s]));
        }
    }

    private void Ordonner()
    {
        for (var s = 0; s < Sources; s++) _ordre[s] = s;
        // Seules les actives se classent du grave a l'aigu ; les rangs eteints restent
        // derriere, ou personne ne les lit.
        Array.Sort(_ordre, 0, Actives, Comparer<int>.Create((a, b) => _hauteurs[a].CompareTo(_hauteurs[b])));
    }

    /// <summary>
    /// Compare les gabarits fraichement appris a ceux d'avant, par cosinus : deux gabarits
    /// qui pointent dans la meme direction decrivent le meme timbre, quelle que soit leur
    /// intensite. Les colonnes restent a leur place parce que chaque apprentissage repart
    /// des gabarits courants au lieu de tirer au hasard.
    /// </summary>
    private void MesurerStabilite()
    {
        if (_profilConnu)
        {
            for (var s = 0; s < Sources; s++)
            {
                double ps = 0, na = 0, nb = 0;
                for (var f = 0; f < Longueur; f++)
                {
                    var a = _w[s * Longueur + f];
                    var b = _profilPrecedent[s * Longueur + f];
                    ps += a * b;
                    na += (double)a * a;
                    nb += (double)b * b;
                }
                var cos = na > Eps && nb > Eps
                    ? (float)(ps / (Math.Sqrt(na) * Math.Sqrt(nb)))
                    : 0f;
                // Un cosinus entre gabarits positifs vaut deja 0,5 pour deux timbres sans
                // rapport : la moitie basse de l'echelle ne distingue rien. On l'etire.
                var net = Clamp01((cos - 0.55f) / 0.40f);
                _stabilite[s] += (net - _stabilite[s]) * 0.35f;
            }
        }

        Array.Copy(_w, _profilPrecedent, _w.Length);
        _profilConnu = true;
    }

    /// <summary>Niveau de la source de rang <paramref name="rang"/>, du grave a l'aigu ; le reste en dernier.</summary>
    public float ActivationOrdonnee(int rang) =>
        rang >= 0 && rang < Actives ? _courant[_ordre[rang]]
        : rang == RangReste ? _reste
        : 0f;

    /// <summary>
    /// Hauteur de la source de rang donne, EN CE MOMENT : la couleur de son gabarit plus la
    /// position ou il joue sur cette image. C'est une vraie hauteur de note, ce que le
    /// profil fige ne pouvait pas donner.
    /// </summary>
    public float HauteurOrdonnee(int rang)
    {
        if (rang == RangReste) return _hauteurReste;
        if (rang < 0 || rang >= Actives) return 0.5f;
        var s = _ordre[rang];
        var position = _courant[s] > Eps ? _positionCourante[s] : _positionsApprises[s];
        return EnOctavesHz(CaseEnHz(_centres[s] + position));
    }

    /// <summary>Une case de l'axe logarithmique, en hertz.</summary>
    public static float CaseEnHz(float caseLog) =>
        ProfileLearner.F0 * MathF.Pow(2f, caseLog / ProfileLearner.ParOctave);

    /// <summary>
    /// Grave et aigu du crate, en hertz. Sept octaves, ce qui couvre du sub au cymbale.
    /// </summary>
    private const float GraveHz = 40f, AiguHz = 10_000f;

    /// <summary>
    /// Convertit un centre de gravite spectral, exprime en fraction de la bande analysee, en
    /// une position perceptive entre 0 et 1 — en octaves, parce que l'oreille compte en
    /// rapports et non en ecarts. Mesure a l'ecran, en lineaire les six contours tenaient
    /// dans les treize pour cent du bas de leur case.
    /// </summary>
    public static float EnOctaves(float rangMoyen, int sampleRate) =>
        EnOctavesHz(rangMoyen * sampleRate * 0.5f);

    public static float EnOctavesHz(float hz)
    {
        if (hz <= GraveHz) return 0f;
        var octaves = MathF.Log2(hz / GraveHz) / MathF.Log2(AiguHz / GraveHz);
        return Clamp01(octaves);
    }

    private static float Clamp01(float x) => x < 0f ? 0f : x > 1f ? 1f : x;
}
