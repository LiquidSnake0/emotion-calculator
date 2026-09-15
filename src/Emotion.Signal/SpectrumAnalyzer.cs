namespace Emotion.Signal;

/// <summary>
/// Transforme une fenetre d'echantillons en une image du signal : niveau, bandes,
/// attaque, tempo.
///
/// Sans etat externe, entierement testable : on lui donne des echantillons, elle rend
/// une <see cref="VisualFrame"/>. Les sources se contentent de la nourrir, qu'elles
/// viennent d'une carte son ou d'un fichier.
/// </summary>
public sealed class SpectrumAnalyzer
{
    /// <summary>Taille de fenetre. 1024 a 48 kHz, soit 21 ms : assez court pour qu'un kick reste net.</summary>
    public const int Window = 1024;

    private readonly int _sampleRate;
    private readonly float[] _hann = Fft.Hann(Window);
    private readonly float[] _re = new float[Window];
    private readonly float[] _im = new float[Window];

    private readonly float[] _prevSpectrum = new float[Window / 2];

    // Maximum glissant par bande, pour normaliser sur ce qui joue plutot que sur une
    // constante. Sans lui, un morceau fort sature les douze bandes a 1 et le visuel
    // n'a plus aucun relief, tandis qu'un morceau feutre ne fait rien bouger.
    private readonly float[] _bandPeak = new float[VisualFrame.BandCount];
    private readonly OnsetDetector _onsets = new();

    // Un detecteur par registre. Ils partagent la mecanique — flux positif, seuil
    // adaptatif, ecart minimal — mais chacun ne regarde que sa tranche de spectre,
    // et chacun se cale donc sur le niveau de bruit qui lui est propre.
    private readonly OnsetDetector _kick = new() { Fermete = FermeteParDefaut };
    private readonly OnsetDetector _clap = new();
    private readonly OnsetDetector _hat = new(minGap: 4);   // les charleys vont vite
    private readonly float[] _prevBand = new float[VisualFrame.BandCount];

    // Deux jeux de bandes, utilises a tour de role. Un tableau neuf a chaque image
    // faisait quarante-sept allocations par seconde, donc des collectes regulieres —
    // et une collecte tombe forcement, un jour, pendant l'ecriture vers l'anneau. Deux
    // suffisent : le consommateur en ligne a fini d'en lire un avant que le suivant ne
    // soit reecrit, et les files bornees n'en gardent au plus que deux.
    private readonly float[][] _bandPool =
    [
        new float[VisualFrame.BandCount],
        new float[VisualFrame.BandCount],
    ];
    private int _bandTurn;

    // Enveloppes lissees des trois registres. Le flux brut est en dents de scie d'une
    // fenetre a l'autre : y chercher un maximum local revient a compter le bruit. Une
    // moyenne mobile courte en fait une enveloppe ou un sommet veut dire quelque chose.
    private readonly float[] _smooth = new float[4];
    /// <summary>
    /// Le tempo. Construit dans le constructeur, avec le taux d'echantillonnage reel.
    ///
    /// IL ETAIT CONSTRUIT ICI, AVEC LE TAUX PAR DEFAUT, ET LE BUG A TENU LONGTEMPS.
    ///
    /// Le constructeur recevait le taux et le transmettait a tous les etages — bandes,
    /// harmonie, voix, timbre — sauf a celui-la, qui gardait 48 kHz quoi qu'il arrive.
    /// Sur un signal a 44,1 kHz, chaque fenetre etait donc supposee durer 21,3 ms alors
    /// qu'elle en dure 23,2 : les periodes d'autocorrelation etaient lues 8,8 % trop
    /// courtes, et le tempo d'autant trop rapide.
    ///
    /// Le meme extrait donnait <b>87,2 BPM a 48 kHz et 92,0 a 44,1</b>. Un tempo qui
    /// depend du taux d'echantillonnage ne mesure pas le morceau, il mesure le fichier.
    /// Et le cas n'a rien de theorique : un vinyle numerise, un logiciel de mix, une carte
    /// son grand public sortent tous du 44,1.
    /// </summary>
    private readonly TempoTracker _tempo;

    // L'harmonie travaille sur une fenetre quatre fois plus longue, pour separer les
    // demi-tons. Elle recoit les memes echantillons et se cadence toute seule.
    private readonly HarmonicAnalyzer _harmony;

    // Ce que les autres ne voient pas : tout ce qui change la couleur du son sans etre
    // une attaque ni un changement d'accord.
    private readonly NoveltyDetector _novelty = new();

    // Les instruments qui ne frappent pas. Ils travaillent sur la moitie harmonique de
    // la separation, qui etait jusqu'ici calculee puis jetee.
    private readonly VoiceTracker _voices;

    /// <summary>Le pipeline des voies, pour que la sonde rapporte ce qu'il a mesure.</summary>
    public SourcePipeline SourcePipeline => _voices.Pipeline;

    /// <summary>Les voies, pour lire leur maturite et leur poser un nom.</summary>
    public VoiceTracker Voix => _voices;

    /// <summary>La derniere image publiee, pour la sonde. Ce que le renderer a recu.</summary>
    public VisualFrame Derniere { get; private set; }

    /// <summary>Ou passe le temps, etage par etage. Eteint par defaut, et alors gratuit.</summary>
    public Etapes Etapes { get; } = new();

    /// <summary>
    /// Employer le domaine complexe plutot que le flux d'energie pour juger une attaque.
    /// Le temps de comparer les deux sur le meme signal.
    /// </summary>
    public bool FluxComplexeActif { get; set; }

    /// <summary>
    /// Part du domaine complexe dans le jugement du kick. Zero le desactive, et il ne coute
    /// alors rien.
    ///
    /// NUL PAR DEFAUT, ET C'EST LA MESURE QUI L'A DECIDE.
    ///
    /// L'idee etait bonne et le mecanisme fonctionne : une note qui commence repart d'une
    /// phase arbitraire, ce qu'un flux d'energie ne voit pas, et c'est exactement ce qui
    /// manque sur une frappe etouffee sous un sample sature.
    ///
    /// Le resultat est reel mais inegal. A poids 0,2, sur quatre morceaux : un enregistrement
    /// de set passe de 14 a 26 % de frappes bien calees et son verrouillage de 58 a 75 % ; un
    /// extrait propre passe de 69 a 80 % de verrouillage. Mais <b>Macroblank, le repertoire de
    /// reference, se degrade</b> — verrouillage de 62 a 49 %, frappes calees de 23 a 18.
    ///
    /// Et le reglage n'est pas stable : entre 0,2 et 0,3, le verrouillage moyen tombe de 64 a
    /// 45 %. Un optimum aussi etroit se regle sur du bruit, pas sur une propriete du signal.
    ///
    /// On garde donc le mecanisme et on le laisse eteint : il est disponible pour une matiere
    /// ou il aide, et il ne degrade rien tant que personne ne l'allume. Le figer a une valeur
    /// de compromis aurait empire le seul disque qu'on connaisse bien.
    /// </summary>
    public float PoidsComplexe { get; set; }

    /// <summary>
    /// Moyenne la courbe du kick sur deux fenetres avant de la juger.
    ///
    /// FAUX PAR DEFAUT, ET C'EST LA PLUS GROSSE CORRECTION DU DETECTEUR.
    ///
    /// Le lissage etait la depuis le debut, pour la raison qui parait evidente : le flux
    /// brut est en dents de scie, et y chercher un sommet reviendrait a compter le bruit.
    /// Sauf que <see cref="Smooth"/> rend <c>(precedent + courant) / 2</c>. Une attaque
    /// qui ne dure qu'une fenetre — c'est-a-dire un kick — en ressort <b>etalee sur deux
    /// fenetres de valeur exactement egale</b>. Or le detecteur exige un maximum local
    /// strict : rien d'aussi haut ni avant ni apres. Deux voisines egales, il rejette.
    ///
    /// Le lissage cense proteger du bruit supprimait donc en priorite les attaques les
    /// plus franches, et ne laissait passer que celles qu'il avait deformees assez pour
    /// les departager — au hasard d'une fenetre pres.
    ///
    /// LA MESURE, SUR QUATRE-VINGT-DIX SECONDES DE TROIS ENREGISTREMENTS.
    ///
    /// <code>
    ///                        lisse        brut
    ///   Macroblank
    ///     intervalles justes   35 %        52 %
    ///     frappes calees       23 %        33 %
    ///     verrouillage         62 %        67 %
    ///     ecart median      1,21 temps   1,00 temps
    ///   enregistrement de set
    ///     verrouillage         58 %        87 %
    ///     frappes calees       14 %        33 %
    ///   instamata
    ///     verrouillage         49 %        73 %
    /// </code>
    ///
    /// L'ecart median est le chiffre qui tranche. Il valait 1,21 temps : ni une noire, ni
    /// une croche, ni rien de musical — la signature d'un detecteur qui rate des frappes
    /// et en invente entre. Il vaut maintenant <b>1,00 temps</b>. Le detecteur bat sur le
    /// temps, ce qu'il n'avait jamais fait.
    ///
    /// LA MARGE A ETE REVERIFIEE, ET ELLE NE BOUGE PAS. Elle avait ete reglee avec le
    /// lissage, donc plus aucune raison d'etre juste sans lui. Balayee de 1,2 a 3,0 : les
    /// intervalles justes de Macroblank culminent a 1,8 (52 %) et se degradent des deux
    /// cotes — 50 % a 1,2, 42 % a 2,2, 38 % a 3,0. La valeur tenait a autre chose qu'au
    /// lissage.
    ///
    /// L'interrupteur reste, pour pouvoir refaire la comparaison sur une autre matiere.
    /// </summary>
    public bool LissageKick { get; set; }

    /// <summary>
    /// Le meme lissage, sur le clap et le charley.
    ///
    /// VRAI PAR DEFAUT — LE MEME DEFAUT, ET POURTANT LA CONCLUSION INVERSE.
    ///
    /// Le raisonnement etait tentant : le lissage etale un pic sur deux fenetres egales et
    /// le maximum local strict les rejette, c'est demontre sur le kick, donc le retirer
    /// partout. Ces deux registres comptent en plus au-dela de leur propre eclair — les
    /// familles de frappes sont nourries par <c>kick || clap || charley</c>, et c'est la
    /// regularite des familles qui permet a la verification croisee du tempo de dire
    /// quelque chose.
    ///
    /// LA MESURE A DIT NON, ET SUR LE SEUL DISQUE QU'ON CONNAISSE BIEN.
    ///
    /// <code>
    ///                             lisse    brut
    ///   Macroblank  accord         0,43    0,01
    ///               verrouillage   67 %    50 %
    ///   instamata   accord         0,00    0,65
    /// </code>
    ///
    /// Les chiffres du kick, eux, ne bougent pas d'un point — 52 % d'intervalles justes et
    /// 33 % de frappes calees dans les deux cas — ce qui confirme au passage que les trois
    /// detecteurs sont bien independants.
    ///
    /// LA RAISON EST DEJA ECRITE PLUS BAS, A PROPOS DES CHARLEYS. Un registre ne se prete a
    /// une lecture franche que s'il <b>se vide entre deux frappes</b>. Le grave se vide ;
    /// les mediums et les aigus de ce repertoire ne se vident jamais, entre le souffle de
    /// bande, le crepitement du vinyle et les nappes. Sans lissage, leur courbe n'est plus
    /// une suite de pics mais du bruit ou chaque fenetre est un maximum local : les
    /// familles 2 et 3 de Macroblank passaient de x1,61 et x2,01 — deux rapports lisibles —
    /// a x2,14 toutes les deux, c'est-a-dire a rien.
    ///
    /// Le lissage n'est donc ni bon ni mauvais en soi. Il coute une attaque franche et il
    /// achete du bruit en moins : le marche est bon la ou le fond est charge, mauvais la
    /// ou le registre respire. Instamata gagne beaucoup a l'enlever, et c'est justement
    /// pourquoi l'interrupteur reste : une autre matiere reglera peut-etre autrement.
    /// </summary>
    public bool LissageAttaques { get; set; } = true;

    /// <summary>
    /// Compare le kick a la mediane de son historique plutot qu'a sa moyenne.
    /// Voir <see cref="OnsetDetector.Median"/>.
    ///
    /// TESTE, UTILE SEUL, NUISIBLE AVEC LE RESTE — donc disponible et eteint.
    ///
    /// Avec le lissage encore en place, la mediane rattrapait une bonne part du defaut :
    /// Macroblank passait de 35 a 48 % d'intervalles justes, le verrouillage du set de 58
    /// a 79 %. C'est logique, les deux corrigent le meme mal par deux bouts.
    ///
    /// Mais <b>cumulee au retrait du lissage, elle depasse la cible</b> : les detections de
    /// Macroblank tombent de 126 a 75, les intervalles justes de 52 a 37 %, les frappes
    /// calees de 33 a 21. La raison tient a la marge : la mediane d'un signal en pics est
    /// bien plus basse que sa moyenne, donc le seuil s'effondre, donc le detecteur
    /// declenche sur la pente et l'ecart minimal bloque ensuite le vrai sommet.
    ///
    /// Il aurait fallu regler la marge en meme temps. Deux parametres qui se compensent se
    /// reglent sur le bruit du jeu d'essai, pas sur une propriete du signal — on s'arrete.
    /// </summary>
    public bool SeuilMedian
    {
        get => _kick.Median;
        set => _kick.Median = value;
    }

    /// <summary>Marge du kick au-dessus du fond. Reglee par la mesure, pas choisie.</summary>
    public float MargeKick
    {
        get => _kick.Margin;
        set => _kick.Margin = value;
    }

    private readonly ComplexFlux _fluxComplexe = new(Window / 2);
    private readonly WhitenedFlux _fluxBlanchi = new(Window / 2);

    /// <summary>Largeur d'une raie, en hertz. Depend du taux d'echantillonnage.</summary>
    private readonly float _binHz;

    /// <summary>Duree d'une fenetre, en millisecondes.</summary>
    private readonly float _frameMs;

    /// <summary>
    /// Instant reel de la derniere frappe retenue, corrige de l'anticipation et de la
    /// position dans la fenetre. C'est cet instant que la grille utilise, et c'est celui
    /// qu'il faut confronter a une implementation de reference.
    /// </summary>
    public long FrappeMs { get; private set; }

    /// <summary>
    /// Position de l'attaque dans la fenetre PRECEDENTE.
    ///
    /// C'est celle-la qui compte : le detecteur juge la fenetre d'avant, jamais celle qui
    /// vient d'arriver. Garder le releve de la fenetre courante pour dater une frappe
    /// jugee sur la precedente revient a corriger une mesure avec le releve d'une autre.
    /// </summary>
    private float _offsetPrec;

    /// <summary>
    /// Part du flux blanchi dans le jugement du kick. Zero le desactive, et il ne coute
    /// alors rien — le blanchiment n'est calcule que s'il sert.
    ///
    /// MESURE SUR TREIZE MORCEAUX, ET LAISSE ETEINT — MAIS PAS POUR LA RAISON QU'ON CROYAIT.
    ///
    /// Le blanchiment permet de regarder plus large que les trois bandes du kick sans se
    /// faire noyer, et c'est theoriquement ce qu'il faut ici : a 44,1 kHz ces trois bandes
    /// ne couvrent que trois raies, de 43 a 172 Hz, ou le kick et la basse tombent ensemble.
    ///
    /// Juge d'abord sur nos propres indicateurs, il paraissait n'aider que deux disques sur
    /// trois — d'ou l'idee, tenace pendant une soiree, que « Macroblank veut un detecteur
    /// etroit et les autres un large ». Rejuge contre aubio, le verdict s'inverse : la part
    /// de nos frappes qui coincide avec une attaque reelle monte sur les trois.
    ///
    /// VALIDATION SUR DIX MORCEAUX NEUFS. Un album entier de Macroblank, jamais servi a
    /// regler quoi que ce soit, 90 s pris au milieu de chaque piste :
    ///
    /// <code>
    ///                        etroit   blanchi 0,8
    ///   justesse (aubio)      +19 p      +28 p     gagne sur 8/10
    ///   regularite             37 %       41 %     gagne sur 7/10
    ///   verrouillage           43 %       36 %     PERD sur 8/10
    /// </code>
    ///
    /// LES DEUX SONT VRAIS, ET C'EST LA LECON. La justesse mesure « est-ce un vrai
    /// evenement », jamais « est-ce le bon ». Le blanchiment trouve davantage d'attaques
    /// reelles — c'est verifie contre une implementation independante — mais ce sont des
    /// attaques quelconques du bas-medium et non la pulsation. La grille recoit alors un
    /// melange de temps et de contretemps, et lache : 84 a 42 % sur une piste, 54 a 20 %
    /// sur une autre.
    ///
    /// Or c'est le verrouillage qui fait le visuel, puisque l'horloge ne peut predire —
    /// donc anticiper le retard — que tant qu'elle tient la grille. On garde donc l'etroit
    /// par defaut, en sachant desormais que ce n'est pas parce qu'il voit mieux.
    ///
    /// CE QUI MANQUE POUR TRANCHER VRAIMENT : une mesure exterieure de la PULSATION, et
    /// non des evenements. Ni la justesse ni le verrouillage ne la donnent — la premiere
    /// ignore la regularite, le second se juge contre une grille calee sur ce qu'il note.
    /// </summary>
    public float PoidsBlanchi { get; set; }

    /// <summary>Jusqu'ou le flux blanchi regarde, en hertz.</summary>
    public float BlanchiHz { get; set; } = 500f;

    /// <summary>
    /// Fraction de temps pendant laquelle le detecteur de kick reste sourd apres avoir
    /// frappe.
    ///
    /// LE MECANISME QU'ELLE CACHE, ET QU'IL FAUT MESURER. Une fois qu'il a tire, le
    /// detecteur est aveugle pendant cette fraction. Si une fausse detection tombe au
    /// quart du temps, le vrai kick qui suit n'est qu'a trois quarts de temps d'elle :
    /// bloque. La detection suivante arrive donc un temps et quart plus tard,
    /// c'est-a-dire de nouveau au quart du temps — et le train reste decale.
    ///
    /// Une seule fausse detection peut ainsi deplacer durablement toute la suite, ce qui
    /// est un candidat serieux pour l'eparpillement des periodes d'une fenetre a l'autre.
    /// </summary>
    public float PartDeTemps { get; set; } = 0.85f;

    /// <summary>
    /// Derniere bande, exclue, sur laquelle le kick est juge. Cinq bandes valent 30 a
    /// 410 Hz ; trois s'arretaient a 144.
    ///
    /// TROIS ETAIT ECRIT DANS LE CODE, CINQ DANS LA DOCUMENTATION, ET C'EST CINQ QUI EST
    /// JUSTE. Le desaccord a survecu parce qu'aucune mesure ne pouvait le trancher : les
    /// indicateurs internes comparent les frappes a une grille calee sur ces memes frappes.
    ///
    /// Mesure contre une verite terrain exterieure — periode du crate, phase par repli
    /// d'energie — sur dix morceaux du bac, concentration de Rayleigh des frappes a la
    /// periode reelle :
    ///
    ///     bandes    3       4       5       6
    ///     R      0,238   0,270   0,286   0,270      (hasard 0,09)
    ///     au-dessus du double du hasard : 5/10, 9/10, 8/10, 7/10 morceaux
    ///
    /// Cinq est un maximum franc. Confirme par une seconde reference exterieure,
    /// <c>aubioonset</c> : l'accord de nos frappes avec de vrais evenements passe de 54,5 a
    /// 59,6 % pour un hasard de 25 %.
    ///
    /// LES TROIS JUGES NE SONT PAS D'ACCORD, ET IL FAUT LE DIRE. <see cref="Emotion.Pulse"/>
    /// voit la stabilite tomber de 48 a 31 % — mais elle cherche elle-meme la periode qui
    /// concentre le mieux, si bien qu'un detecteur qui pulse regulierement AILLEURS qu'au
    /// temps y excelle. C'est exactement la distinction que la verite terrain permet enfin
    /// de faire, et c'est elle qui tranche : la projection suit le temps de la musique,
    /// pas la periode la plus commode.
    ///
    /// La bande 4 sert alors aussi au clap, qui commence a 4. Le chevauchement est couvert
    /// par la garde de dominance existante — un clap ne compte que si le medium l'emporte
    /// franchement sur le grave. Mesure sur macro : 80 a 85 kicks, 85 a 75 claps, 353
    /// charleys inchanges.
    /// </summary>
    public int BandesKick { get; set; } = 5;

    /// <summary>
    /// Part du flux du medium versee dans l'enveloppe du tempo, en plus du registre du kick.
    /// </summary>
    public float PoidsMedium { get; set; } = DefautPoidsMedium;

    /// <summary>
    /// Valeur par defaut, tiree de la mesure et non d'un principe.
    ///
    /// Part du tempo publie sur l'album du bac, amorce par la fiche, et ecart au tempo du
    /// crate :
    ///
    ///     med       0        0,08      0,15
    ///     publie   54 %      79 %      78 %
    ///     ecart    0,7 %     0,6 %     0,7 %
    ///
    /// Les morceaux les plus feutres sont ceux qui gagnent le plus — t04 de 22 a 92 %, t09
    /// de 24 a 88, t06 (« Passepartout », que le DJ a signale) de 27 a 58. Ce sont
    /// exactement ceux ou le grave n'a pas d'attaque franche.
    ///
    /// HUIT CENTIEMES ET NON QUINZE, A CAUSE D'UN SEUL MORCEAU. t11 tourne a 59,87 BPM et
    /// ne publiait deja qu'un tempo sur vingt fenetres ; a 0,15 il se tait completement, a
    /// 0,08 il garde ses trois pour cent. Le gain moyen est le meme, et l'on ne perd pas
    /// un morceau entier pour un point de moyenne.
    /// </summary>
    public const float DefautPoidsMedium = 0.08f;

    /// <summary>
    /// La phase de la grille vient du repli de l'energie du medium. Voir <see cref="PhaseFold"/>.
    ///
    /// Coupe, la grille se cale comme avant sur les seules frappes detectees — c'est-a-dire
    /// au hasard : 190 ms d'ecart au vrai temps pour un hasard de 172.
    /// </summary>
    public bool ReplierPhase { get; set; } = true;

    /// <summary>
    /// Les frappes detectees continuent-elles de tirer la grille. Garde par defaut : le
    /// repli donne la position, les frappes disent qu'il se passe quelque chose, et l'on
    /// n'a pas de raison mesuree de renoncer aux secondes.
    /// </summary>
    public bool CalerSurFrappes { get; set; } = true;

    /// <summary>Memoire du repli, en temps.</summary>
    public float MemoireRepli
    {
        get => _repli.Memoire;
        set => _repli.Memoire = value;
    }

    /// <summary>De combien les frappes doivent l'emporter pour renverser le sommet du medium.</summary>
    public float AvantageFrappe
    {
        get => _repli.Avantage;
        set => _repli.Avantage = value;
    }

    /// <summary>Le repli lui-meme, pour la sonde.</summary>
    public PhaseFold Repli => _repli;

    /// <summary>
    /// Force minimale d'un kick, en fraction de la force habituelle des precedents.
    /// Voir <see cref="OnsetDetector.Fermete"/>.
    /// </summary>
    public float FermeteKick
    {
        get => _kick.Fermete;
        set => _kick.Fermete = value;
    }

    /// <summary>Valeur retenue par la mesure. Voir <see cref="OnsetDetector.Fermete"/>.</summary>
    public const float FermeteParDefaut = 0.55f;

    /// <summary>La derniere valeur du flux blanchi, pour la sonde.</summary>
    public float DernierFluxBlanchi => _fluxBlanchi.DernierTotal;
    private readonly EventProfiler _evenements = new(Window / 2);

    /// <summary>L'etendue de contour apprise par chaque rang. Voir <see cref="ContourRange"/>.</summary>
    private readonly ContourRange _etendues;

    /// <summary>Ce que les rangs ont appris de leur propre etendue. Pour la sonde.</summary>
    public ContourRange Etendues => _etendues;

    /// <summary>Les familles de frappes rencontrees sur ce disque, pour la sonde.</summary>
    public EventProfiler Evenements => _evenements;

    /// <summary>
    /// A quel point les familles de frappes confirment le tempo, 0 a 1.
    ///
    /// C'est la seule verification du tempo qui ne vienne pas de lui-meme : les familles
    /// sont formees sur le timbre et n'ont jamais consulte la grille.
    /// </summary>
    public GridAgreement Accord => _accord;

    private readonly GridAgreement _accord;
    private long _dernierJugement;

    /// <summary>Les deux fonctions de detection, pour les comparer sur la meme image.</summary>
    public float DernierFluxEnergie { get; private set; }
    public float DernierFluxComplexe { get; private set; }

    /// <summary>
    /// Ce que la fiche du cue affirmait du tempo, confronte a ce que le disque fait.
    ///
    /// Poser <c>Reference.Expected</c> depuis la fiche evite de recalculer ce qui a deja
    /// ete etabli au casque — et fait apparaitre la derive si le plateau s'ecarte.
    /// </summary>
    public TempoReference Reference { get; } = new();

    /// <summary>
    /// Reprend ce qu'on savait deja de ce disque : les portraits des sources et le tempo.
    ///
    /// Ce qui suit corrigera ces valeurs au lieu de les remplacer. C'est ce qui fait
    /// qu'arreter un morceau et le relancer ne perd rien, et que les seize temps du cue
    /// servent encore une fois le disque passe au master.
    /// </summary>
    /// <summary>
    /// Part du disque entrant deja passee au master, 0 a 1. Au-dela de zero et en deca de
    /// un, les deux disques sonnent ensemble : les voies suivent mais n'apprennent plus.
    /// </summary>
    public float Fondu
    {
        set => _voices.Apprend = value <= 0.02f || value >= 0.98f;
    }

    public void Reprendre(in TrackKnowledge connaissance)
    {
        _voices.Reprendre(connaissance);
        if (connaissance.Bpm > 0f) Amorcer(connaissance.Bpm);
    }

    /// <summary>
    /// Ce que la fiche du crate annonce pour le disque qui arrive.
    ///
    /// La fiche est la premiere source d'information du systeme : c'est de la qu'on part.
    /// Elle ne remplace jamais la mesure — un vinyle se joue au fader et un BPM stocke est
    /// faux des la premiere seconde — mais elle dit dans quel voisinage chercher, et cela
    /// change tout. Sur un album du crate, le tempo publie passe de 43 a 83 % de justesse.
    /// Voir <see cref="TempoTracker.Preferer"/>.
    /// </summary>
    public void Amorcer(float bpm)
    {
        if (bpm <= 0f) return;
        Reference.Expected = bpm;
        _tempo.Preferer(bpm);
    }

    /// <summary>Ce qu'on sait a cet instant, pret a etre range pour la prochaine ecoute.</summary>
    public TrackKnowledge Connaissance(string id, in TrackKnowledge precedente) =>
        new(id,
            _voices.Portraits(),
            _tempo.Bpm ?? precedente.Bpm,
            precedente.BpmObservations + 1,
            precedente.SecondsHeard + _secondesEcoutees);

    private float _secondesEcoutees;

    // LA SEPARATION PAR LE TIMBRE, ET NON PAR LA FREQUENCE.
    //
    // Un piano et un saxophone qui jouent la meme octave tombent dans la meme bande :
    // aucune finesse de decoupage ne les separe, on additionne leurs niveaux et l'on
    // obtient une grandeur qui ne decrit ni l'un ni l'autre. Ils ne partagent pas leur
    // timbre, et c'est par la qu'on les prend.
    private readonly SourceSeparator _separation;

    // La couleur du son. Elle travaille sur le spectre complet et non sur le percussif :
    // un filtre passe-bas agit sur tout, et le mesurer apres separation reviendrait a
    // regarder par le trou de la serrure.
    private readonly TimbreTracker _timbre;

    // Separation harmonique / percussive. Les bandes et les attaques travaillent alors
    // sur le percussif seul : le piano ne remplit plus les mediums de flux, et un clap
    // redevient detectable pour ce qu'il est.
    //
    // COUPEE PAR DEFAUT DEPUIS QU'ON L'A MESUREE SUR PLUSIEURS SOURCES.
    //
    // Elle a ete introduite pour empecher le piano de declencher les claps, et elle le
    // fait — le nombre de claps monte de 91 a 113 quand on la coupe. Mais elle coute
    // beaucoup plus qu'elle ne rapporte des qu'on regarde ailleurs.
    //
    // Verrouillage du temps fort, avec puis sans, sur quatre passages : 20 puis 86 % sur
    // un Macroblank, 84 puis 49 sur un passage du set, 52 puis 60, 35 puis 43. Moyenne
    // 48 contre 60, et surtout <b>pire cas 20 contre 43</b> — c'est le pire cas qui
    // decide en direct, un passage ou le systeme ignore le temps fort quatre fois sur
    // cinq se voyant bien davantage qu'une moyenne.
    //
    // Et elle coute la moitie de la latence d'analyse : <b>43 ms avec, 21 sans</b>.
    //
    // La raison de fond tient au repertoire. Le barber beats etouffe et filtre ses kicks
    // jusqu'a les noyer ; ce que la separation retient comme percussif y est surtout du
    // crepitement de bande, qui n'a ni periode ni accent. Elle retire du grave la basse,
    // laquelle porte la pulsation autant que la frappe.
    //
    // Reste activable par `Signal__Separate=true`, et le restera : sur un repertoire aux
    // kicks francs et au piano bavard, le calcul pourrait s'inverser.
    private readonly Hpss? _hpss;

    // La grille metrique et la tension. Elles ne regardent aucun echantillon : elles ne
    // consomment que ce que les autres ont deja conclu. C'est le premier etage du projet
    // qui travaille sur le temps long — huit mesures — la ou tout le reste vit dans
    // l'instant.
    private readonly BeatGrid _grid = new();
    private readonly ArcDetector _arc = new();

    // La structure longue. Elle autocorrele une signature de mesure la ou TempoTracker
    // autocorrele une enveloppe d'attaque : meme idee, un ordre de grandeur au-dessus.
    private readonly SectionTracker _section = new();

    // Le disque joue-t-il toujours ce qu'on croit qu'il joue. Sans elle, un saut de sillon
    // laisse la grille decrire pendant plus d'une minute un endroit du disque ou l'on
    // n'est plus, son oubli etant lent par construction.
    private readonly ContinuityWatch _continuity = new();

    // Le temps fort cherche dans ce que portent les quatre temps, et non dans ce qui s'y
    // declenche. Un kick present sur les quatre temps n'apprend rien ; le fait que celui
    // du premier soit plus appuye, si.
    private readonly DownbeatProfile _profile = new();

    // Deux jeux publies a tour de role : rien n'est alloue par image.
    private readonly float[][] _sepPool =
        [new float[SourceSeparator.Sources], new float[SourceSeparator.Sources]];
    private readonly LaneState[][] _lanePool =
        [new LaneState[SourceSeparator.Sources], new LaneState[SourceSeparator.Sources]];
    private readonly float[][] _hautPool =
        [new float[SourceSeparator.Sources], new float[SourceSeparator.Sources]];
    private int _sepTurn;

    // Ou tombe l'attaque a l'interieur de la fenetre. Sans lui, un kick au premier
    // echantillon et un kick au dernier sont annonces au meme instant, a 21 ms pres.
    private readonly TransientLocator _transient = new();

    // Les gestes ramenes a des etats stables. Ce qui flotte n'est pas la valeur d'un
    // filtre mais la reponse a « est-il ferme », et c'est elle qui fait clignoter un motif.
    private readonly GestureTracker _gestures = new();

    // L'AMORTISSEMENT DESCEND DANS L'ANALYSE.
    //
    // Il vivait dans le renderer, ou chaque grandeur continue traversait un ressort avant
    // d'etre dessinee. L'unite CUDA aurait du les reimplementer tous, avec les memes
    // raideurs — et une regle qui vit en deux endroits finit par vivre de deux facons,
    // comme le Camelot avant elle.
    //
    // Les raideurs sont celles qu'employait le renderer, pour que le mouvement ne change
    // pas en changeant de place. Aucune latence n'est ajoutee : le ressort existait deja.
    private readonly Damper _dLevel = new(16f);
    private readonly Damper[] _dBands =
    [
        new(20f), new(20f), new(20f), new(20f), new(20f), new(20f),
        new(20f), new(20f), new(20f), new(20f), new(20f), new(20f),
    ];
    private readonly Damper _dLow = new(14f);    // la basse est lourde, elle traine
    private readonly Damper _dMid = new(22f);
    private readonly Damper _dHigh = new(40f);   // le xylophone est vif

    /// <summary>
    /// Duree d'une fenetre, en secondes. Elle depend du taux d'echantillonnage et ne peut
    /// donc pas etre une constante : 21,3 ms a 48 kHz, 23,2 a 44,1. Une constante y
    /// faisait avancer tous les ressorts 8,8 % trop vite sur un signal a 44,1.
    /// </summary>
    private readonly float _frameSeconds;

    // Le seuil de connaissance : quand annoncer au telephone qu'on en sait assez sur le
    // disque en cours pour que le GPU puisse basculer dessus.
    private readonly KnowledgeGate _gate = new();

    // Les bandes suivent une echelle logarithmique : l'oreille entend le rapport entre
    // deux frequences, pas leur difference. Douze bandes lineaires donneraient onze
    // bandes d'aigus et une seule pour tout le grave.
    private readonly int[] _edges;
    private readonly PhaseFold _repli = new();

    /// <summary>Ce qui se repete, et tous les combien. Voir <see cref="MotifTracker"/>.</summary>
    private readonly MotifTracker _motif = new();

    /// <summary>Le suivi de motif, pour la sonde et le rendu.</summary>
    public MotifTracker Motif => _motif;

    /// <summary>
    /// Crete propre a chaque source. Elle monte tout de suite et oublie lentement.
    ///
    /// Un maximum brut serait fixe par le premier accident venu et vaudrait pour toute la
    /// soiree — le projet a deja paye ce piege ailleurs. Le retour vaut 0,9995 par fenetre,
    /// soit une demi-vie d'environ trente secondes : assez long pour qu'une source garde son
    /// echelle a travers un couplet, assez court pour qu'un changement de disque la refasse.
    /// </summary>
    private readonly float[] _creteSource = new float[SourceSeparator.Sources];

    private const float CreteRetour = 0.9995f;

    /// <summary>Plancher, pour qu'une source silencieuse ne soit pas amplifiee en bruit.</summary>
    private const float CretePlancher = 1e-3f;
    private SourceEnvelope? _enveloppes;

    private readonly float[] _prevMag = new float[Window / 2];
    private float _fluxMedium;

    /// <param name="separate">
    /// Separer le percussif de l'harmonique avant analyse. Coute la latence annoncee par
    /// <see cref="Hpss.LatencyFrames"/>, soit 64 ms sur le reglage par defaut.
    /// </param>
    /// <param name="memoireTempoS">
    /// Duree observee par l'autocorrelation du tempo. Elle borne par le bas le temps
    /// d'accroche : on ne peut rien dire d'une periode avant d'en avoir entendu plusieurs.
    /// </param>
    /// <param name="inertieTempo">
    /// Inertie de la courbe de score du tempo. Stabilite d'un cote, reactivite de l'autre.
    /// </param>
    public SpectrumAnalyzer(int sampleRate = 48_000, bool separate = false,
                            float memoireTempoS = TempoTracker.DefautMemoireS,
                            float inertieTempo = TempoTracker.DefautInertie,
                            float tempoPrefere = 90f, float largeurPreference = 0.25f,
                            float memoireSeparationS = SourceSeparator.MemoireDefautS)
    {
        _sampleRate = sampleRate;
        _frameSeconds = Window / (float)sampleRate;
        _binHz = sampleRate / (float)Window;
        _frameMs = _frameSeconds * 1000f;
        _tempo = new TempoTracker(sampleRate, Window, memoireTempoS, inertieTempo,
                                  tempoPrefere, largeurPreference);
        _accord = new GridAgreement(_evenements.Familles);
        _edges = BuildEdges(sampleRate);
        _harmony = new HarmonicAnalyzer(sampleRate);
        _voices = new VoiceTracker(sampleRate, Window);
        // La separation apprend sur la duree du cue — quarante secondes par defaut. Les
        // tests, eux, la raccourcissent : ils n'ont pas quarante secondes a donner.
        _separation = new SourceSeparator(sampleRate, Window,
            memoire: (int)MathF.Max(1f, memoireSeparationS * sampleRate / Window));
        _motifs = new MotifSources(SourceSeparator.Sources);
        _etendues = new ContourRange(SourceSeparator.Sources);
        _timbre = new TimbreTracker(sampleRate, Window);
        // Trois fenetres et non sept : le retard tombe de 64 a 21 ms. La separation est
        // un peu moins nette, mais elle reste tres suffisante pour empecher le piano de
        // declencher les claps — et surtout elle cesse de desynchroniser le visuel.
        _hpss = separate ? new Hpss(Window / 2, timeFrames: 3, freqBins: 17) : null;
    }

    /// <summary>La separation est-elle active.</summary>
    public bool Separating => _hpss is not null;

    /// <summary>Ce que le profil des quatre temps designe, pour le reglage.</summary>
    public (int Offset, float Confidence, IReadOnlyList<float> Scores, int GridBeat) Downbeat =>
        (_profile.Offset, _profile.Confidence, _profile.Scores, _grid.Beat);

    /// <summary>Les sources separees par le timbre, du grave a l'aigu.</summary>
    public SourceSeparator Separation => _separation;

    /// <summary>Le taux d'echantillonnage sur lequel tout ceci est calcule.</summary>
    public int Taux => _sampleRate;

    /// <summary>La courbe d'autocorrelation du tempo, pour le reglage.</summary>
    public IEnumerable<(float Bpm, float Raw, float Score)> TempoPeaks(int take = 6) =>
        _tempo.Peaks(take);

    /// <summary>Correlation brute a un tempo donne. Pour verifier une hypothese connue.</summary>
    public float TempoRawAt(float bpm) => _tempo.RawAt(bpm);

    /// <summary>Ce que le systeme sait du disque en cours, et s'il en sait assez.</summary>
    public Readiness Readiness => _gate.Current;

    /// <summary>Un nouveau disque commence : tout est a reapprendre.</summary>
    /// <summary>
    /// Un autre disque commence. LA SEPARATION REPART DE ZERO, et elle ne le faisait pas :
    /// `Reset` existait sans etre appele nulle part, si bien que les profils du disque
    /// precedent servaient de point de depart au suivant. Sur quarante secondes de memoire,
    /// cela aurait voulu dire quarante secondes a decrire le mauvais disque.
    /// </summary>
    private readonly MotifSources _motifs;
    private readonly ushort[][] _motifPool = [new ushort[SourceSeparator.Sources], new ushort[SourceSeparator.Sources]];
    private readonly float[][] _caracterePool = [new float[SourceSeparator.Sources], new float[SourceSeparator.Sources]];
    private readonly int[][] _degrePool = [new int[SourceSeparator.Sources], new int[SourceSeparator.Sources]];
    private readonly int[][] _platinePool = [new int[SourceSeparator.Sources], new int[SourceSeparator.Sources]];

    /// <summary>
    /// L'IDENTITE DES PLATINES. Le disque qui joue est sur la platine 1 au depart ; celui qui
    /// entre est sur l'autre, et au retrait il devient « celui qui joue » SANS changer de
    /// platine. C'est ce que le rendu attend : une scene par platine, qui ne saute pas de
    /// cote quand le fondu se termine.
    /// </summary>
    private int _platineCourante = 1;
    private int _platineEntrante;
    public int PlatineJoue => _platineCourante;
    public int PlatineEntre => _separation.EnFondu ? _platineEntrante : 0;
    private bool[]? _gamme;
    private float _accordGamme;

    /// <summary>La fiche Camelot du disque : la gamme devient un prior doux et les degres se publient.</summary>
    public void Gamme(string? camelot)
    {
        _gamme = Emotion.Signal.Gamme.Classes(camelot);
        _separation.Gamme(camelot);
        _accordGamme = 0f;
    }
    public float AccordGamme => _accordGamme;
    public string? Camelot => _separation.Camelot;

    /// <summary>Le motif de chaque source et sa stabilite, pour la sonde.</summary>
    public MotifSources Motifs => _motifs;

    public void NewTrack()
    {
        _gate.Reset();
        _separation.Reset();
        _motifs.Reset();
        _enveloppes?.Reset();
        _etendues.Oublier();
        Array.Clear(_creteSource);
        _rythmeSu = false;
        _bpmCue = _bpmEntrant = 0f;
        _entrante = null;
    }

    // ------------------------------------------------------------------ le relais

    /// <summary>
    /// LE ROLE : le cue apprend, le master joue. En master, la separation ne forme jamais de
    /// portrait ; elle recoit ceux du cue par <see cref="Accueillir"/>. Seul, l'analyseur
    /// fait tout lui-meme, comme quand il n'y a qu'une entree.
    /// </summary>
    public RoleAnalyseur Role
    {
        get => _role;
        set { _role = value; _separation.SuiviSeul = value == RoleAnalyseur.Master; }
    }
    private RoleAnalyseur _role;

    /// <summary>
    /// L'EMPREINTE D'UN DISQUE, telle que le cue la transmet au master : le portrait de la
    /// separation, puis pour chaque case publiee (les gabarits, et le reste en dernier) son
    /// motif, son caractere, sa crete et son etendue melodique ; le tempo appris et la fiche.
    /// </summary>
    public sealed record EmpreinteDisque(
        SourceSeparator.Empreinte Sources,
        float[][] Motifs, float[] StabiliteMotifs, int[] MesuresVues,
        float[] Caracteres, bool[] CaractereConnu,
        float[] Cretes, (float Bas, float Haut, int Vues)[] Etendues,
        float Bpm, string? Camelot)
    {
        /// <summary>Cases publiees : les gabarits, plus le reste.</summary>
        public int Publiees => Motifs.Length;
    }

    public EmpreinteDisque Empreinte()
    {
        var sources = _separation.Portrait();
        var n = _separation.Publiees;
        var motifs = new float[n][]; var stab = new float[n]; var vues = new int[n];
        var car = new float[n]; var connu = new bool[n]; var cretes = new float[n];
        var et = new (float, float, int)[n];
        for (var r = 0; r < n; r++)
        {
            (motifs[r], stab[r], vues[r]) = _motifs.Exporter(r);
            (car[r], connu[r]) = _enveloppes?.Exporter(r) ?? (0.5f, false);
            cretes[r] = _creteSource[r];
            et[r] = _etendues.Exporter(r);
        }
        return new EmpreinteDisque(sources, motifs, stab, vues, car, connu, cretes, et, _tempo.Bpm ?? 0f, _separation.Camelot);
    }

    private EmpreinteDisque? _entrante;
    private float _bpmCue, _bpmEntrant;
    private bool _rythmeSu;

    /// <summary>
    /// ACCUEILLE le disque qui entre : ses regles rejoignent celles du disque qui joue, et le
    /// suivi porte les deux. Ce que l'analyseur tient par rang — motifs, caractere, crete,
    /// etendue — suit les rangs que le relais a deplaces, et les rangs du disque entrant
    /// recoivent ce que le cue avait mesure pour eux. Le reste est partage : il garde ce
    /// qu'il avait, et prendra celui du disque entrant au retrait de l'autre.
    /// </summary>
    public void Accueillir(EmpreinteDisque e)
    {
        var avant = _separation.Actives;
        var resteAvant = _separation.RangReste;
        var rel = _separation.AccueillirPortrait(e.Sources);
        _enveloppes ??= new SourceEnvelope(SourceSeparator.Sources, _frameSeconds);
        Deplacer(rel, avant, resteAvant);
        for (var r = 0; r < SourceSeparator.Sources; r++)
        {
            var rE = rel.RangEntrant[r];
            if (rE < 0 || rE >= e.Publiees) continue;
            _motifs.Importer(r, e.Motifs[rE], e.StabiliteMotifs[rE], e.MesuresVues[rE]);
            _enveloppes.Importer(r, e.Caracteres[rE], e.CaractereConnu[rE]);
            _creteSource[r] = e.Cretes[rE];
            _etendues.Importer(r, e.Etendues[rE].Bas, e.Etendues[rE].Haut, e.Etendues[rE].Vues);
        }
        // Le reste, quand rien ne jouait : il vient du disque entrant tout de suite.
        if (avant == 0 && e.Publiees > e.Sources.Actives) PoserReste(e, _separation.RangReste);
        _entrante = e;
        // La reference du pitch ne change qu'au retrait : pendant le fondu, c'est encore le
        // disque qui joue que l'on mesure. Le premier disque de la soiree n'attend pas.
        _bpmEntrant = e.Bpm;
        if (avant == 0) _bpmCue = e.Bpm;
        // Le premier disque de la soiree arrive sur la platine courante ; les suivants sur
        // l'autre, en alternance.
        _platineEntrante = avant == 0 ? _platineCourante : 3 - _platineCourante;
        if (e.Camelot is not null) Gamme(e.Camelot);
    }

    /// <summary>
    /// RETIRE le disque qui sortait : celui qui est entre devient le disque qui joue, et le
    /// reste prend le motif et le caractere que le cue lui avait mesures.
    /// </summary>
    public void Retirer()
    {
        var avant = _separation.Actives;
        var resteAvant = _separation.RangReste;
        var rel = _separation.RetirerDisque(0);
        Deplacer(rel, avant, resteAvant);
        if (_entrante is { } e && e.Publiees > e.Sources.Actives) PoserReste(e, _separation.RangReste);
        _entrante = null;
        _rythmeSu = false;
        if (_bpmEntrant > 0f) _bpmCue = _bpmEntrant;
        _bpmEntrant = 0f;
        // Le disque entre devient celui qui joue, et garde sa platine.
        if (_platineEntrante != 0) _platineCourante = _platineEntrante;
        _platineEntrante = 0;
    }

    private void PoserReste(EmpreinteDisque e, int rangReste)
    {
        if (rangReste < 0) return;
        var rE = e.Sources.Actives;   // le reste est la derniere case publiee de l'empreinte
        _motifs.Importer(rangReste, e.Motifs[rE], e.StabiliteMotifs[rE], e.MesuresVues[rE]);
        _enveloppes?.Importer(rangReste, e.Caracteres[rE], e.CaractereConnu[rE]);
        _creteSource[rangReste] = e.Cretes[rE];
        _etendues.Importer(rangReste, e.Etendues[rE].Bas, e.Etendues[rE].Haut, e.Etendues[rE].Vues);
    }

    /// <summary>Deplace l'etat par rang selon le relais ; le reste suit sa case.</summary>
    private void Deplacer(SourceSeparator.Relais rel, int activesAvant, int resteAvant)
    {
        var map = new int[SourceSeparator.Sources];
        Array.Fill(map, -1);
        for (var r = 0; r < rel.Actives; r++) map[r] = rel.AncienRang[r];
        var resteApres = _separation.RangReste;
        if (resteApres >= 0 && resteAvant >= 0) map[resteApres] = resteAvant;
        _motifs.Reorganiser(map);
        _enveloppes?.Reorganiser(map);
        _etendues.Reorganiser(map);
        var cretes = new float[SourceSeparator.Sources];
        for (var r = 0; r < map.Length; r++) if (map[r] >= 0) cretes[r] = _creteSource[map[r]];
        Array.Copy(cretes, _creteSource, cretes.Length);
    }

    /// <summary>Le pitch : le tempo mesure rapporte a celui que le cue a appris, 1 sans relais.</summary>
    public float Pitch =>
        _bpmCue > 0f && _tempo.Bpm is { } b && b > 0f ? b / _bpmCue : 1f;

    /// <summary>Le rythme est-il su (le verrou rapide, celui du boom-tchak) ?</summary>
    public bool RythmeSu => _rythmeSu;

    /// <summary>Derniere rupture de continuite constatee, pour le journal.</summary>
    public string LastBreak => _continuity.Reason;

    /// <summary>
    /// Ecart de la derniere frappe a la grille, en fraction de temps. C'est la seule
    /// grandeur qui mesure la <b>justesse de la phase</b> — le verrouillage et l'ecart
    /// entre frappes, eux, sont comptes a la fenetre et ne peuvent pas la voir.
    /// </summary>
    public float SyncError => _grid.LastSyncError;

    /// <summary>Periode de la grille, en millisecondes. A comparer au tempo publie.</summary>
    public float GridBeatMs => _grid.BeatMs;

    /// <summary>Position de l'attaque dans la fenetre courante, en millisecondes.</summary>
    public float TransientOffsetMs => _transient.OffsetMs;

    /// <summary>Une rupture vient d'etre constatee sur cette fenetre.</summary>
    public bool ContinuityBroken => _continuity.Broken;

    /// <summary>L'etat du suivi de structure longue, pour le reglage.</summary>
    public (int Bars, float Best, IReadOnlyList<float> Scores) Section =>
        (_section.PhraseBars, _section.BestScore, _section.Scores);

    /// <summary>Les pentes de la tension, pour le reglage et la sonde hors ligne.</summary>
    public (float Bright, float Bass, float Busy) Slopes =>
        (_arc.SlopeBright, _arc.SlopeBass, _arc.SlopeBusy);

    /// <summary>
    /// Retard total entre le son et la detection, en millisecondes. La somme des deux
    /// etages : separation puis recherche de sommet.
    ///
    /// Il doit rester sous quarante millisecondes, seuil au-dela duquel l'oeil cesse de
    /// lier une image au son qui l'a declenchee. C'est une grandeur qu'on affiche, pas
    /// qu'on subit.
    /// </summary>
    public float LatencyMs =>
        ((_hpss?.LatencyFrames ?? 0) + OnsetDetector.Lookahead) * 1000f / _sampleRate * Window;

    /// <summary>Tempo estime, nul tant que la detection n'a pas accroche.</summary>
    public float? Bpm => _tempo.Bpm;

    /// <summary>
    /// Reprend le tempo d'un autre analyseur comme point de depart. Voir
    /// <see cref="TempoTracker.Adopt"/> : c'est une amorce, pas un verrou, et
    /// l'analyse du master continue de chercher a partir de la.
    /// </summary>
    /// <summary>
    /// Reprend le tempo trouve par un autre analyseur, ET RECENTRE LA PREFERENCE DESSUS.
    /// Mesure sur Dead Internet (91 BPM) → Glyph Chamber (74) : adopter 74 sans recentrer
    /// laissait la preference resserree par le verrou rythme autour de 91, et le suivi
    /// retombait a 97 — le triolet de 74 — d'ou un pitch publie de +31 % sur un fader
    /// immobile. Le tempo qu'on adopte est la fiche du disque qui arrive : on cherche autour.
    /// </summary>
    public void AdoptTempo(float bpm, long tMs)
    {
        _tempo.Adopt(bpm, tMs);
        _tempo.Preferer(bpm);
        // Le pitch se rapporte au disque dont on vient d'adopter le tempo : des la mi-fondu,
        // c'est celui qui entre. Sinon il annoncait −19 % pendant dix secondes, le temps que
        // le retrait change la reference — un fader immobile ne doit pas bouger le pitch.
        if (_separation.EnFondu && _bpmEntrant > 0f) _bpmCue = _bpmEntrant;
    }

    /// <summary>
    /// Analyse une fenetre. <paramref name="samples"/> doit contenir
    /// <see cref="Window"/> echantillons mono dans [-1, 1].
    /// </summary>
    public VisualFrame Analyze(ReadOnlySpan<float> samples, long tMs)
    {
        if (samples.Length != Window)
            throw new ArgumentException($"fenetre de {Window} echantillons attendue", nameof(samples));

        Etapes.Debut();
        var harmony = _harmony.Feed(samples);
        Etapes.Fin(0);

        // Le releve de la fenetre qui vient d'etre jugee, avant de le remplacer par celui
        // de la fenetre qui arrive. Voir _offsetPrec : c'est l'ancien qui date la frappe.
        _offsetPrec = _transient.OffsetMs;
        _transient.Feed(samples, _sampleRate);
        Etapes.Fin(1);

        var sum = 0f;
        for (var i = 0; i < Window; i++)
        {
            var s = samples[i];
            sum += s * s;
            _re[i] = s * _hann[i];
            _im[i] = 0f;
        }

        // Racine de la moyenne des carres, puis compression : un signal musical vit
        // dans le bas de l'echelle lineaire, et un visuel qui suit le RMS brut reste
        // ecrase en permanence.
        var rms = MathF.Sqrt(sum / Window);
        var level = Clamp01(MathF.Pow(rms * 3.2f, 0.55f));

        Fft.Forward(_re, _im);

        var half = Window / 2;
        Span<float> spectrum = stackalloc float[half];
        for (var i = 0; i < half; i++)
            spectrum[i] = MathF.Sqrt(_re[i] * _re[i] + _im[i] * _im[i]);

        // LE FLUX DU MEDIUM, BIN PAR BIN, POUR LE REPLI DE PHASE.
        //
        // Il ne peut pas se tirer des douze bandes, et la mesure a coute deux tentatives
        // pour l'admettre. Agreger avant de differencier fait s'annuler, dans une meme
        // bande, une partielle qui monte contre une qui descend — il ne reste presque rien
        // de la ponctuation qui porte le temps. On differencie donc chaque bin, puis on
        // somme les hausses : c'est l'ordre inverse, et c'est le seul qui garde le signal.
        //
        // Compression logarithmique avant la difference, pour la meme raison qu'ailleurs :
        // sans elle les passages forts ecrasent tout le reste et le repli ne suit plus que
        // les cretes.
        var mid0 = Math.Min(half, _edges[3]);      // 144 Hz
        var mid1 = Math.Min(half, _edges[8]);      // 1969 Hz
        _fluxMedium = 0f;
        for (var i = mid0; i < mid1; i++)
        {
            var c = MathF.Log(1f + spectrum[i] * 8f);
            var d = c - _prevMag[i];
            if (d > 0f) _fluxMedium += d;
            _prevMag[i] = c;
        }

        // La separation remplace le spectre par sa seule composante percussive. Tant
        // que son tampon n'est pas plein elle ne rend rien, et on travaille alors sur
        // le spectre complet : mieux vaut une analyse imparfaite qu'un ecran noir
        // pendant les premieres fenetres.
        // Le timbre se mesure avant la separation, sur le spectre entier.
        // La separation rend les deux composantes. On garde la percussive pour les
        // bandes et les attaques, et on donne l'harmonique aux registres tonals : sans
        // elle, chaque coup de caisse claire ferait bondir les trois a la fois.
        // Le domaine complexe se calcule ici, tant que la transformee est encore intacte :
        // il lui faut la phase, que le module a jetee. Rien n'est calcule quand son poids
        // est nul — il coute une racine, un arc-tangente et un cosinus par bin.
        if (PoidsComplexe > 0f) _fluxComplexe.Feed(_re, _im, Math.Min(half, _edges[3]));

        var voices = Voices.None;
        Span<float> full = stackalloc float[half];
        spectrum.CopyTo(full);
        Etapes.Fin(2);                       // fenetrage, FFT et module

        if (_hpss is not null && _hpss.Feed(spectrum))
        {
            Etapes.Fin(3);                   // separation harmonique / percussive
            voices = _voices.Feed(_hpss.Harmonic);
            Etapes.Fin(4);                   // registres
            _hpss.Percussive.CopyTo(spectrum);
        }
        else
        {
            // LES REGISTRES TONALS NE DEPENDENT PAS DE LA SEPARATION.
            //
            // Ils n'etaient calcules que dans sa branche, si bien que la couper les a
            // supprimes d'un coup — et avec eux le disque des graves, le polygone de la
            // voix et les triangles des aigues, soit les deux tiers de ce qui se voit.
            // Le DJ l'a dit en trois mots : « il manque plein de sons ».
            //
            // Sans separation, on leur donne le spectre entier. C'est moins net qu'une
            // moitie harmonique — un coup de caisse claire fera bouger les trois
            // registres a la fois — mais infiniment preferable au silence.
            Etapes.Fin(3);                   // separation coupee : rien a compter
            voices = _voices.Feed(full);
            Etapes.Fin(4);
        }

        // Les six niveaux publies deviennent les six sources separees : ils decrivent des
        // timbres et non des tranches de frequence. Les agregats grave/medium/aigu
        // continuent de venir des registres, qui les rendent mieux.
        if (_separation.Pret)
        {
            var act = _sepPool[_sepTurn];
            var haut = _hautPool[_sepTurn];
            _sepTurn ^= 1;

            // CHAQUE SOURCE SUR SA PROPRE ECHELLE, ET NON EN CONCURRENCE AVEC LES AUTRES.
            //
            // Le niveau publie etait l'activation divisee par le MAXIMUM DE L'INSTANT parmi
            // les six. Les sources se battaient donc image par image : celle qui dominait
            // valait un, les autres tombaient vers zero — et une source discrete ne pouvait
            // exister a cote d'une source forte.
            //
            // Mesure sur un morceau du bac : le niveau median des six valait 0,000, et
            // chacune se declarait absente entre quarante et soixante pour cent du temps.
            // Le DJ l'a dit autrement : « quelque chose qui se desactive a de la peine a se
            // rallumer ». Pour revenir, il lui fallait rivaliser avec la dominante.
            //
            // C'est une faute de principe et pas un seuil mal regle. Chaque source agit sur
            // elle-meme ; elle se sert des autres pour s'informer, jamais pour se mesurer.
            // Elle est donc rapportee a SA PROPRE CRETE, qui suit ce qu'elle monte et
            // oublie lentement ce qu'elle ne fait plus — la meme mecanique que ContourRange
            // emploie deja pour l'etendue melodique, et pour la meme raison.
            for (var i = 0; i < SourceSeparator.Sources; i++)
            {
                var brute = _separation.ActivationOrdonnee(i);
                _creteSource[i] = brute > _creteSource[i]
                    ? brute
                    : _creteSource[i] * CreteRetour;
                act[i] = Clamp01(brute / MathF.Max(CretePlancher, _creteSource[i]));
                // La hauteur mesuree porte sur huit octaves ; ce qu'on affiche, c'est la
                // place de la source dans l'etendue qu'elle parcourt vraiment. Voir
                // ContourRange : sans cela, une source qui ne couvre qu'une octave et
                // demie se deplace dans un cinquieme de sa case.
                haut[i] = _etendues.Situer(i, _separation.HauteurOrdonnee(i), act[i] > 0.12f);
            }

            // CE QUI EST MESURE DOIT DECRIRE CE QUI EST AFFICHE.
            //
            // Les niveaux et les contours publies viennent de la separation par timbre ; la
            // nettete doit donc en venir aussi. Elle etait prise sur les bandes d'octave,
            // c'est-a-dire sur autre chose que ce que l'ecran montrait — et comme une bande
            // d'octave est presque toujours partagee, la jauge restait basse en decrivant un
            // objet que personne ne regardait.
            var etats = _lanePool[_sepTurn];
            var platines = _platinePool[_sepTurn];
            for (var i = 0; i < SourceSeparator.Sources; i++)
            {
                var brut = _voices.EtatDe(i);
                // LA PLATINE, PAS LE ROLE. Le separateur dit « celui qui joue / celui qui
                // entre » ; le rendu veut une identite qui ne saute pas de cote au relais.
                platines[i] = i >= _separation.Publiees ? 0
                    : _separation.DisqueOrdonne(i) switch { 0 => _platineCourante, 1 => _platineEntrante, _ => 3 };

                // L'ENVELOPPE SE MESURE SUR CE QUI EST AFFICHE, pour la meme raison que la
                // nettete : elle etait prise sur les bandes d'octave, c'est-a-dire sur
                // autre chose que ce que l'ecran montre. Une source qui se separe bien mais
                // dont la bande d'octave est partagee aurait recu l'enveloppe de sa voisine.
                _enveloppes ??= new SourceEnvelope(SourceSeparator.Sources, _frameSeconds);
                _enveloppes.Tempo(_tempo.Bpm);
                _enveloppes.Feed(i, act[i]);

                // LA FRAPPE AUSSI VIENT DE LA SOURCE. Elle restait celle du registre de
                // meme rang — le seul champ de la case que la separation ne remplacait pas,
                // et le morse publie etait celui d'une bande de frequence, pas d'une source.
                etats[i] = brut with
                {
                    Level = act[i],
                    Position = haut[i],
                    Hit = _enveloppes.Frappe(i),
                    Dominance = _separation.DominanceOrdonnee(i),
                    Heard = _separation.EcouteOrdonnee(i),
                    Sharpness = _separation.StabiliteOrdonnee(i),
                    Pique = _enveloppes.Pique(i),
                    Tenue = _enveloppes.Tenue(i),
                    Retrait = _enveloppes.Muet(i),
                    Platine = platines[i],
                };
            }

            // Les cases publiees comptent le reste : la derniere case, c'est ce que les
            // gabarits n'expliquent pas — sur ce repertoire, la batterie.
            // LE MOTIF DE CHAQUE SOURCE, ET LE VERROU. La position dans la mesure vient de
            // la grille ; chaque source y range ses montees. Quand deux sources tiennent leur
            // motif sur huit mesures, le morceau est su : la separation ne reapprend plus.
            var publiees = _separation.Publiees;
            var phaseMotif = _grid.Beat < 0 ? (float?)null : (_grid.Beat + _grid.Phase) / 4f;
            for (var i = 0; i < publiees; i++) _motifs.Feed(i, act[i], phaseMotif);
            var masques = _motifPool[_sepTurn];
            var caracteres = _caracterePool[_sepTurn];
            var degres = _degrePool[_sepTurn];
            for (var i = 0; i < SourceSeparator.Sources; i++)
            {
                masques[i] = i < publiees ? _motifs.Masque(i) : (ushort)0;
                caracteres[i] = i < publiees ? _enveloppes.Caractere(i) : 0.5f;
                degres[i] = i < publiees ? _separation.DegreOrdonne(i) : Emotion.Signal.Gamme.Inconnu;
            }
            // L'ACCORD DE LA FICHE AVEC CE QU'ON ENTEND : la part du chromagramme dans la
            // gamme, moyennee sur vingt secondes. Une fiche fausse se voit ici.
            if (_gamme is { } gm && harmony.Chroma is { Length: 12 } chroma)
            {
                float dedans = 0, tout = 0;
                for (var k = 0; k < 12; k++) { tout += chroma[k]; if (gm[k]) dedans += chroma[k]; }
                if (tout > 1e-6f) _accordGamme += (dedans / tout - _accordGamme) * (1f - MathF.Exp(-_frameSeconds / 20f));
            }
            if (!_separation.Verrou && _motifs.Verrouille(publiees)) _separation.Verrouiller();

            // LE VERROU A DEUX VITESSES. Le rythme se sait apres trois mesures de boom-tchak
            // tenu : on resserre alors la preference de tempo autour de ce qu'on mesure, et
            // l'on ne cherche plus que la variance — le pitch que le DJ pousse. Les sonorites,
            // elles, continuent de s'apprendre jusqu'a ce que leurs motifs tiennent.
            if (!_rythmeSu && _separation.RangReste is var rr && rr >= 0 && _motifs.VerrouRythme(rr))
            {
                _rythmeSu = true;
                if (_tempo.Bpm is { } bpmSu) _tempo.Preferer(bpmSu);
            }

            voices = voices with { Levels = act, Pitches = haut, Lanes = etats,
                                   Actives = publiees, Motifs = masques, Verrou = _separation.Verrou,
                                   Caracteres = caracteres, Degres = degres, AccordGamme = _accordGamme,
                                   Platines = platines, PlatineJoue = _platineCourante,
                                   PlatineEntre = _separation.EnFondu ? _platineEntrante : 0,
                                   VerrouRythme = _rythmeSu, Pitch = Pitch };
        }

        // Flux spectral positif : on ne compte que ce qui monte. Une note qui s'eteint
        // n'est pas une attaque.
        //
        // Il est calcule sur le seul registre du kick, pas sur tout le spectre, et
        // c'est un choix dicte par le repertoire. Le barber beats est plein de souffle,
        // de crepitement de vinyle et de nappes qui bougent : ce bruit remplit les
        // aigus de flux en permanence et noie la seule montee qui compte. Mesure sur
        // instamata : en pleine bande, le detecteur voyait quatre attaques par temps.
        var kickBins = Math.Min(half, _edges[KickBandLimit]);

        var flux = 0f;
        for (var i = 0; i < half; i++)
        {
            var d = spectrum[i] - _prevSpectrum[i];
            if (d > 0 && i < kickBins) flux += d;
            _prevSpectrum[i] = spectrum[i];
        }

        var fluxC = _fluxComplexe.DernierTotal;

        // Le flux blanchi regarde plus large que les trois bandes du kick, parce que le
        // blanchiment lui permet de le faire sans se noyer. Voir WhitenedFlux.
        if (PoidsBlanchi > 0f)
            _fluxBlanchi.Feed(spectrum, 1, Math.Min(half, (int)(BlanchiHz / _binHz)));

        DernierFluxEnergie = flux;
        DernierFluxComplexe = fluxC;

        var onset = _onsets.Feed(FluxComplexeActif ? fluxC : flux);
        Etapes.Fin(6);                       // flux spectral et detection d'attaque
        // Meme correction que pour la grille : l'attaque a ete jugee sur la fenetre
        // precedente, elle se date donc a l'endroit ou elle y tombait.
        if (onset) _tempo.Mark(tMs - (long)_frameMs + (long)_offsetPrec);

        var bands = _bandPool[_bandTurn];
        _bandTurn ^= 1;
        for (var b = 0; b < bands.Length; b++)
        {
            var lo = _edges[b];
            var hi = _edges[b + 1];
            var peak = 0f;
            for (var i = lo; i < hi && i < half; i++)
                if (spectrum[i] > peak) peak = spectrum[i];

            // Le pic plutot que la moyenne : sur une bande large, une moyenne noie une
            // pointe unique, or c'est justement la pointe qui se voit a l'ecran.
            //
            // Puis normalisation sur le maximum recent de cette bande, qui redescend
            // lentement. C'est un controle de gain : le visuel garde son relief que le
            // morceau soit pousse ou feutre, sans que le DJ ait a toucher a un niveau.
            _bandPeak[b] = MathF.Max(peak, _bandPeak[b] * PeakDecay);
            var reference = MathF.Max(_bandPeak[b], MinReference);
            bands[b] = Clamp01(MathF.Pow(peak / reference, 0.7f));
        }

        // Flux par registre, calcule sur les bandes deja normalisees : chaque detecteur
        // se cale ainsi sur le contraste de sa tranche et non sur son volume absolu,
        // ce qui evite qu'un mix charge en graves eteigne la detection des claps.
        //
        // Les tranches ne se chevauchent plus : la bande 3 appartenait aux deux, et un
        // kick y bavait assez pour declencher le detecteur de clap. Mesure a l'ecran de
        // diagnostic : 80 kicks et 81 claps, tombant aux memes instants.
        // LE KICK SE JUGE SUR DEUX SIGNAUX, PAS UN.
        //
        // La montee des bandes voit ce qui monte en energie ; elle est aveugle a une frappe
        // etouffee sous un sample sature, ce qui est la moitie du repertoire. Le domaine
        // complexe, lui, voit qu'une note repart d'une phase arbitraire meme quand son
        // amplitude bouge peu. Les deux sont ramenes a un rapport sans dimension avant
        // d'etre melanges, sinon le poids du melange dependrait du volume du disque.
        var brutKick = BandRise(bands, 0, BandesKick)
                     + PoidsComplexe * _fluxComplexe.Rapport
                     + PoidsBlanchi * _fluxBlanchi.Rapport;
        var rKick = LissageKick ? Smooth(0, brutKick) : brutKick;
        var brutClap = BandRise(bands, 4, 9);
        var rClap = LissageAttaques ? Smooth(1, brutClap) : brutClap;
        // LES CHARLEYS GARDENT LA DIFFERENCE, ET CE N'EST PAS UNE EXCEPTION DE CONFORT.
        //
        // Un rapport n'est informatif que dans un registre qui se vide entre deux frappes.
        // Or les aigus de ce repertoire ne se vident jamais : le souffle de bande et le
        // crepitement de vinyle y entretiennent un plancher permanent — c'est deja ce qui
        // avait force a limiter le flux au registre du kick. Le masque y reste donc haut,
        // le rapport ne depasse jamais un, et la mesure est sans appel : 23 charleys au
        // lieu de 302 sur cent secondes.
        //
        // Le grave et le medium, eux, se vident entre deux frappes. Ils prennent le
        // rapport, qui les rend invariants au niveau ; les aigus gardent la difference.
        var brutHat = BandRise(bands, 9, VisualFrame.BandCount, ratioMode: false);
        var rHat  = LissageAttaques ? Smooth(2, brutHat) : brutHat;

        // LE TEMPO SE MESURE AVANT LA SEPARATION, SUR LE SPECTRE ENTIER.
        //
        // C'est le meme raisonnement que pour le timbre, et il a fallu une verite terrain
        // pour le voir. Sur un morceau de Macroblank donne a 90 BPM, la separation
        // faisait disparaitre la pulsation : correlation 0,066 a 90 BPM une fois separe,
        // contre 0,226 a 85 BPM sur le signal complet. Le pic dominant tombait a 108, un
        // tempo qui n'existe pas dans ce morceau.
        //
        // La raison tient au repertoire. Le barber beats etouffe et filtre ses kicks
        // jusqu'a les noyer ; ce que HPSS retient comme « percussif » y est alors surtout
        // du crepitement de bande, qui n'a aucune periode. La pulsation, elle, est portee
        // par le morceau <b>entier</b> — par la basse, par les accords qui pulsent, par
        // tout ce que la separation met de cote.
        //
        // La separation reste indispensable ailleurs : sans elle, le piano declenche les
        // claps. Elle sert donc a decider <i>ce qui frappe</i>, jamais a decider <i>a
        // quelle vitesse ca tourne</i>.
        var pulse = 0f;
        for (var i = 1; i < kickBins; i++) pulse += full[i];
        Etapes.Fin(7);                       // les douze bandes
        // CE QUI NOURRIT L'ESTIMATEUR DE TEMPO, ET POURQUOI LE GRAVE SEUL NE SUFFIT PAS.
        //
        // Il ne voyait que 30 a 410 Hz, et a travers un rapport au masque — c'est-a-dire la
        // meme combinaison qui a rendu le repli de phase inutile : un masque fait pour
        // detecter des evenements efface justement la periodicite d'un motif regulier.
        //
        // Sur les morceaux joues aux instruments continus, ou l'attaque est molle et le
        // grave etouffe, cela se paie cash. Mesure sur « Passepartout », que le DJ a
        // signale : sans fiche le moteur annonce 115,3 BPM la ou l'analyse Python en trouve
        // 87,89 a cent pour cent de stabilite ; avec la fiche il trouve 86,7 mais ne le
        // publie que sur vingt-sept pour cent des fenetres. Le tempo n'est pas faux, il est
        // absent — et un tempo absent clignote a l'ecran.
        //
        // Le medium porte la pulsation de ce repertoire mieux que le grave : c'est deja ce
        // qu'a montre le repli de phase, 60 ms d'erreur contre 231. On lui en donne donc
        // une part, en flux brut par bin.
        _tempo.Feed(Smooth(3, PulseRise(pulse)) + PoidsMedium * _fluxMedium);
        Etapes.Fin(8);                       // autocorrelation du tempo

        // L'ECART MINIMAL SUIT LE TEMPO, IL N'EST PLUS UNE CONSTANTE.
        //
        // Quatre-vingt-cinq centiemes de temps : assez pour refuser tout ce qui tombe entre
        // deux temps — les 0,75 temps qui dominaient les mesures — sans refuser un temps
        // dont la frappe arrive un peu tot. Un kick legerement en avance reste un kick ;
        // une frappe aux trois quarts du temps n'en est pas un.
        _kick.Suivre(_tempo.Bpm, _frameSeconds * 1000f, PartDeTemps);

        var kick = _kick.Feed(rKick);
        var clap = _clap.Feed(rClap);

        // Un clap ne compte que si le medium l'emporte franchement sur le grave a cet
        // instant. Sinon c'est le corps du kick qu'on entend monter dans le medium, et
        // l'eclair partirait sur le kick — ce qui est exactement ce qu'il ne faut pas.
        if (clap && rClap < rKick * 1.3f) clap = false;

        var hits = new Hits(kick, clap, _hat.Feed(rHat));

        // L'empreinte de la frappe, prise sur la fenetre ou elle tombe. Elle ne sert pas a
        // decider qu'il y a eu une frappe — cela vient d'etre fait — mais a savoir laquelle,
        // sans avoir a la nommer.
        var famille = _evenements.Feed(full, kick || clap || hits.Hat);

        // La verification croisee : les familles n'ont jamais consulte le tempo, donc leur
        // accord avec lui vaut quelque chose. On ne rejuge qu'une fois par seconde — c'est
        // une mediane sur soixante instants, pas une grandeur qui bouge d'une image a
        // l'autre.
        if (famille >= 0) _accord.Frappe(famille, tMs);
        if (tMs - _dernierJugement > 1000)
        {
            _dernierJugement = tMs;
            _accord.Juger(_tempo.Bpm);
        }
        Etapes.Fin(9);                       // kick, clap, charley

        UpdateMasks(bands);
        Array.Copy(bands, _prevBand, bands.Length);

        // Ce que rapporte le diagnostic est l'enveloppe du <b>kick</b> et son seuil, pas
        // le flux global : ce sont les enveloppes par registre qui decident des attaques,
        // et un ecran qui afficherait une autre grandeur ferait regler a cote. Un outil
        // de reglage qui montre autre chose que ce qui decide est pire qu'aucun outil.
        var scale = MathF.Max(_kick.Threshold * 2f, 1e-6f);

        _novelty.Feed(bands);

        // Densite : tout ce qui s'est declenche sur cette fenetre. Un passage calme
        // n'est pas seulement plus sourd, il est plus vide.
        var eventCount = (hits.Kick ? 1 : 0) + (hits.Clap ? 1 : 0) + (hits.Hat ? 1 : 0)
                       + (voices.LowHit ? 1 : 0) + (voices.MidHit ? 1 : 0)
                       + (voices.HighHit ? 1 : 0);
        // La separation prend les ECHANTILLONS, pas le spectre d'analyse : elle a sa propre
        // transformee, plus longue, et son propre axe. Le suivi seul se fait ici ;
        // l'apprentissage des gabarits tourne dans un fil de fond et ne compte pas dans le
        // temps d'une image. C'est tout l'interet de l'avoir sorti.
        _separation.Feed(samples);
        Etapes.Fin(5);                       // suivi des timbres separes

        var timbre = _timbre.Feed(full, eventCount);
        Etapes.Fin(10);                      // couleur du son

        // Les grandeurs continues partent amorties ; les evenements restent bruts. Une
        // impulsion lissee n'est plus une impulsion, et c'est le renderer qui les
        // declenche — la seule chose qu'il calcule encore.
        var dt = _frameSeconds;
        level = _dLevel.Feed(level, dt);
        for (var i = 0; i < bands.Length; i++) bands[i] = _dBands[i].Feed(bands[i], dt);
        voices = voices with
        {
            Low = _dLow.Feed(voices.Low, dt),
            Mid = _dMid.Feed(voices.Mid, dt),
            High = _dHigh.Feed(voices.High, dt),
        };

        // Les graves sont pris sur les bandes normalisees et non sur le RMS : c'est leur
        // poids relatif qui compte, et un morceau joue fort ne doit pas paraitre plus
        // structure qu'un autre.
        var bass = (bands[0] + bands[1] + bands[2]) / 3f;

        // La grille metrique. Elle est nourrie de conclusions, jamais de signal : le kick
        // la recale, le kick, le clap et le changement d'accord votent pour le temps fort,
        // et une rupture de section realigne la phrase.
        // Chaque indice vote pour le temps le plus proche de l'instant ou il tombe, et
        // non pour « le temps en cours » : le kick arrive a quelques millisecondes de la
        // frontiere, et le moindre flottement de la grille le ferait changer de camp.
        _arc.Feed(bass, timbre.Centroid, timbre.Density);
        var stepped = _grid.Advance(tMs, _tempo.Bpm);
        if (stepped) _arc.Advance();

        // OU TOMBE LE TEMPS : L'ENERGIE DU MEDIUM, REPLIEE SUR LA PERIODE CONNUE.
        //
        // La grille tenait sa phase des seules frappes detectees, et cette phase etait au
        // niveau du hasard — 190 ms du vrai temps sur dix morceaux du bac, quand un tirage
        // au sort en donne 172. Le repli en rend 69.
        //
        // Le medium et non le grave, et c'est la mesure qui l'impose contre l'intuition :
        // replie sur le grave, l'energie designe le temps a 231 ms pres ; sur le medium, a
        // 60. Le registre du kick reste le meilleur pour dire QU'UNE attaque a lieu, et il
        // est le pire pour dire OU EST le temps.
        //
        // Bandes 3 a 8, soit 144 a 1969 Hz : la voix, la caisse claire, le piano, tout ce
        // qui ponctue. On garde le rapport au masque, comme le kick, pour que le repli ne
        // suive pas le volume du disque.
        if (ReplierPhase)
        {
            // LE FLUX BRUT PAR BIN, ET SURTOUT PAS LE RAPPORT AU MASQUE.
            //
            // Le rapport au masque est fait pour detecter des EVENEMENTS : il compare chaque
            // bande a une crete qui la suit et s'efface entre deux frappes. C'est
            // exactement ce qu'il faut pour dire qu'une attaque a lieu, et exactement ce
            // qu'il ne faut pas pour trouver une periode — un motif regulier de meme
            // amplitude n'y produit presque rien, puisque le masque a appris a l'attendre.
            // Nourri du rapport, le repli tombait a 169 ms du vrai temps, soit le hasard.
            _repli.Feed(tMs, _fluxMedium, _tempo.Bpm, hits.Kick);
            _grid.Recaler(_repli.PhaseDuTemps, _repli.Relief);
        }

        // LA GRILLE SE CALE SUR L'INSTANT REEL DE LA FRAPPE, ET LE CALCUL ETAIT FAUX.
        //
        // L'intention etait bonne : ne pas dater une frappe de l'instant de la fenetre qui
        // la contient, mais de l'endroit ou elle tombe dedans. Le calcul, lui, se trompait
        // deux fois et dans le meme sens.
        //
        //   1. La frappe est jugee sur la fenetre PRECEDENTE — c'est tout le role de
        //      OnsetDetector.Lookahead, qui attend la fenetre suivante pour confirmer un
        //      sommet. Il fallait donc retrancher une fenetre, pas partir de celle-ci.
        //   2. `_transient.OffsetMs` decrit la fenetre COURANTE. On corrigeait la position
        //      d'une frappe avec le releve d'une autre fenetre.
        //
        // On ajoutait donc une dizaine de millisecondes la ou il fallait en retirer une
        // vingtaine.
        //
        // AUCUNE MESURE INTERNE NE POUVAIT LE VOIR, et c'est la lecon. Tous nos indicateurs
        // comparent les frappes a la grille, laquelle se cale sur ces memes frappes : un
        // decalage commun aux deux est invisible par construction. Il a fallu confronter
        // nos instants a ceux d'une autre implementation — les attaques d'aubio, ecart
        // median :
        //
        //                      avant      apres
        //     metronome     +17,3 ms    +4,9 ms
        //     macro         +26,8 ms   +14,2 ms
        //     live          +26,5 ms   +13,0 ms
        //     instamata     +29,7 ms   +18,8 ms
        //
        // Le metronome tranche : on savait y etre exact vis-a-vis de notre propre grille, et
        // l'on y etait pourtant en retard de dix-sept millisecondes sur le monde.
        //
        // La part de nos frappes tombant a moins de vingt millisecondes d'une attaque
        // d'aubio passe de 17 a 43 % sur Macroblank, pour un niveau de hasard de 21 % :
        // c'est-a-dire de sous le hasard a deux fois le hasard.
        //
        // Un retard constant ne se voit pas a l'ecran tant qu'on ne compare rien — mais la
        // grille s'en sert pour se caler, et l'horloge a verrouillage de phase predit ses
        // temps a partir de cette grille. Vingt millisecondes de biais sur l'origine
        // deviennent vingt millisecondes de retard sur chaque temps annonce.
        var at = tMs - (long)_frameMs + (long)_offsetPrec;
        if (hits.Kick)
        {
            FrappeMs = at;
            if (CalerSurFrappes) _grid.Sync(at);
            _grid.MarkKick(at);
        }
        if (hits.Clap) _grid.MarkClap(tMs);
        if (harmony.Change > ChordChangeVote) _grid.MarkChange(tMs);
        if (_novelty.Onset) _grid.MarkSection(tMs);

        // Le profil se nourrit du continu : l'energie grave, la montee du registre du
        // kick, le mouvement harmonique. Aucune de ces trois n'est un evenement.
        _profile.Feed(_grid.BeatIndex, bass, rKick, harmony.Change);
        if (_grid.BarStart && _profile.Confidence > 0.2f)
            _grid.MarkProfile(_profile.Offset, 1.5f * _profile.Confidence);

        _continuity.Feed(level, hits.Kick, _grid.LastSyncError);
        if (_continuity.Broken)
        {
            // ON ATTENUE, ON N'EFFACE PAS.
            //
            // Un effacement complet part du principe que la detection de rupture ne se
            // trompe jamais. Elle se trompe : mesuree a l'origine, elle voyait dix
            // ruptures en cent secondes sur un set qui n'en contenait aucune, et chacune
            // remettait a zero un temps fort qui avait demande une minute d'ecoute.
            //
            // En attenuant fortement, une vraie rupture laisse la nouvelle information
            // l'emporter en quelques mesures, tandis qu'une fausse ne coute qu'un peu de
            // confiance passagere. On jette de meme ce qui decrit une position, jamais ce
            // qui decrit une vitesse : un saut de sillon laisse le meme disque au meme
            // tempo.
            _grid.Weaken(0.5f);
            _section.Reset();
            _arc.Reset();
            _profile.Reset();
            if (_continuity.WasSilence) { _tempo.Reset(); _gate.Reset(); }
        }

        // CE QUI SE REPETE, BANDE PAR BANDE. Mesure avant d'etre ecrit : le melange des
        // douze bandes ne porte pas le motif — une fois sur dix — quand la meilleure bande
        // seule le porte huit fois sur dix. Chaque bande calcule donc chez elle, puis passe
        // sa valeur a ses voisines.
        _motif.Feed(tMs, _tempo.Bpm, bands);

        // La signature de la mesure en cours, close a chaque debut de mesure.
        _section.Feed(bands, timbre.Centroid, timbre.Density);
        if (_grid.BarStart) _section.CloseBar();

        var beat = _grid.Beat;
        var inBar = beat < 0 ? 0f : (beat + _grid.Phase) / 4f;
        var bars = _section.PhraseBars;
        var bar = _section.BarInPhrase;
        var structure = new Structure(
            beat,
            bar,
            (bar + inBar) / bars,
            _grid.Confidence,
            _arc.Buildup,
            // La rupture ne vaut que pour la fenetre ou elle est constatee. L'arc n'avance
            // qu'une fois par temps, et republier son verdict a chaque fenetre ferait durer
            // un evenement instantane une trentaine d'images — la sonde en comptait vingt
            // au meme instant.
            stepped && _arc.Drop,
            _grid.BarStart,
            _grid.BarStart && bar == 0,
            bars,
            _section.BarsToBoundary,
            _section.Confidence,
            _continuity.Trust,
            // La phase du temps, celle qui sert au rendu a anticiper. Elle est publiee
            // meme quand le « 1 » est inconnu : savoir ou l'on est dans le temps ne
            // demande pas de savoir quel temps c'est.
            _grid.Phase);

        var gestures = _gestures.Feed(timbre.Openness, bass, timbre.Density);
        var readiness = _gate.Feed(tMs, _tempo.Bpm, timbre.Centroid, bass, timbre.Density);

        _secondesEcoutees = tMs / 1000f;

        // La fiche du cue, confrontee au disque. Sans fiche, la derive reste nulle et rien
        // ne change : on ne fabrique pas d'ecart avec une reference qu'on n'a pas.
        Reference.Feed(_tempo.Bpm, tMs);

        Etapes.Fin(11);                      // structure, gestes, nouveaute
        Etapes.Image();

        return Derniere = new VisualFrame(
            // LA PHASE VIENT DE LA GRILLE, PLUS DE L'ESTIMATEUR.
            //
            // Elle etait remplie par TempoTracker.Phase, dont l'origine est remise a zero
            // <b>a chaque attaque retenue</b>. Ce n'etait donc pas une position dans la
            // mesure mais un temps ecoule depuis le dernier coup entendu — et comme les
            // coups tombent a peu pres a chaque temps, la valeur ne montait jamais.
            //
            // Mesure sur le repertoire, seize cases de 0 a 1 :
            //
            //     31 29 26 8 2 1 0 0 0 0 0 0 0 0 0 0 %
            //
            // Elle ne depassait pas 0,35. Tout ce que le renderer anime « plus lentement
            // que l'attaque » ne parcourait qu'un tiers de son cycle avant de repartir.
            //
            // BeatGrid existe precisement pour cela : son origine ne bouge pas a chaque
            // coup, elle avance seule et se corrige, et son « 1 » est vote. `inBar` en est
            // la position dans la mesure de quatre temps — c'est-a-dire ce que la
            // documentation de ce champ decrivait depuis le debut.
            tMs, level, bands, onset, beat < 0 ? null : inBar, _tempo.Bpm,
            Hits: hits,
            Harmony: harmony,
            Voices: voices,
            Timbre: timbre,
            Structure: structure,
            Gestures: gestures,
            Readiness: readiness,
            Novelty: _novelty.Level,
            NoveltyOnset: _novelty.Onset,
            EventFamily: famille,
            GridAgreement: _accord.Accord,
            // CE QUI SE REPETE. Zero quand rien n'est designe, et c'est une reponse : « des
            // la premiere ecoute on a cet indice, puis quand on l'entend une deuxieme fois
            // on sait que c'est un refrain ». La premiere fois, l'information n'existe pas.
            MotifPeriode: _motif.Meilleure() is var bm && bm >= 0 ? _motif.Periode(bm) : 0,
            MotifCertitude: _motif.Meilleure() is var bc && bc >= 0 ? _motif.Certitude(bc) : 0f,
            MotifBande: Math.Max(0, _motif.Meilleure()),
            EventPrint: _evenements.Derniere,
            Flux: Clamp01(rKick / scale),
            Threshold: Clamp01(_kick.Threshold / scale),
            ExpectedBpm: Reference.Expected,
            TempoDrift: Reference.Drift,
            DriftVisible: Reference.Visible,
            AnnouncedBpm: Reference.Announcement,
            TempoAnnounce: Reference.Announced);
    }

    /// <summary>
    /// Bornes des bandes, de 30 Hz a 16 kHz, reparties geometriquement.
    /// </summary>
    private static int[] BuildEdges(int sampleRate)
    {
        const float lowHz = 30f, highHz = 16_000f;
        var n = VisualFrame.BandCount;
        var edges = new int[n + 1];
        var binHz = sampleRate / (float)Window;

        for (var i = 0; i <= n; i++)
        {
            var hz = lowHz * MathF.Pow(highHz / lowHz, i / (float)n);
            edges[i] = Math.Max(1, (int)(hz / binHz));
        }

        // Deux bornes egales donneraient une bande vide : on force la croissance.
        for (var i = 1; i <= n; i++)
            if (edges[i] <= edges[i - 1]) edges[i] = edges[i - 1] + 1;

        return edges;
    }

    /// <summary>
    /// Moyenne mobile a deux termes sur l'enveloppe d'un registre. Deux et pas plus :
    /// au-dela, l'attaque s'etale et le sommet se deplace, donc l'effet visuel arrive
    /// en retard sur ce qu'on entend.
    /// </summary>
    /// <summary>
    /// Montee de l'energie grave du spectre complet, en rapport contre un masque — meme
    /// mecanique que pour les registres, et pour la meme raison : un rapport est invariant
    /// au niveau, un masque se laisse depasser par ce qui est plus fort.
    /// </summary>
    private float PulseRise(float energy)
    {
        var ratio = energy / (_pulseMask + 1f);
        if (_pulseAge >= MaskHold) _pulseMask *= MaskDecay;
        if (energy > _pulseMask) { _pulseMask = energy; _pulseAge = 0; }
        _pulseAge++;

        return ratio > 1f ? ratio - 1f : 0f;
    }

    private float _pulseMask;
    private int _pulseAge;

    private float Smooth(int slot, float v)
    {
        var s = (_smooth[slot] + v) * 0.5f;
        _smooth[slot] = v;
        return s;
    }

    /// <summary>Montee d'energie sur une tranche de bandes, depuis la fenetre precedente.</summary>
    /// <summary>
    /// Montee d'un registre, mesuree comme un <b>rapport</b> et non comme une difference.
    ///
    /// C'est le choix de <c>bonk~</c>, le detecteur d'attaques de Puckette, et il tient a
    /// une propriete simple : un rapport est invariant au niveau. Un kick doux dans un
    /// passage doux produit la meme montee qu'un kick fort dans un passage fort, tandis
    /// qu'une difference ne voit que le second.
    ///
    /// C'est exactement le probleme rencontre ici — un mix charge en graves qui eteignait
    /// la detection des claps — traite a sa source plutot que contourne par une
    /// normalisation.
    ///
    /// LE RAPPORT SE PREND CONTRE UN MASQUE, JAMAIS CONTRE LA FENETRE PRECEDENTE.
    ///
    /// Les deux emprunts a <c>bonk~</c> sont indissociables, et l'avoir ignore s'est vu
    /// tout de suite : rapporte a la fenetre precedente, qui peut etre quasi nulle, le
    /// rapport explose sur du bruit. Mesure : les charleys tombaient de 331 a 121 sur le
    /// meme passage.
    ///
    /// Le masque, lui, suit la crete de la bande — il monte instantanement avec le signal,
    /// se maintient quelques fenetres, puis decroit. C'est une reference stable, et un
    /// rapport pris contre lui veut dire quelque chose.
    ///
    /// Il remplace du meme coup l'ecart minimal, qui interdisait toute detection pendant
    /// vingt fenetres sans nuance : une frappe forte suivant de peu une frappe faible
    /// etait perdue. Le masque, lui, se laisse depasser par ce qui est plus fort. Il est
    /// musical la ou la regle etait administrative.
    /// </summary>
    private float BandRise(float[] bands, int from, int to, bool ratioMode = true)
    {
        var rise = 0f;
        for (var i = from; i < to && i < bands.Length; i++)
        {
            if (ratioMode)
            {
                var ratio = bands[i] / (_bandMask[i] + RiseFloor);
                if (ratio > 1f) rise += ratio - 1f;
            }
            else
            {
                var d = bands[i] - _prevBand[i];
                if (d > 0) rise += d;
            }
        }

        return rise;
    }

    /// <summary>
    /// Met a jour le masque de chaque bande : maintien puis decroissance, et remontee
    /// immediate des que le signal depasse.
    /// </summary>
    private void UpdateMasks(float[] bands)
    {
        for (var i = 0; i < bands.Length; i++)
        {
            if (_maskAge[i] >= MaskHold) _bandMask[i] *= MaskDecay;
            if (bands[i] > _bandMask[i]) { _bandMask[i] = bands[i]; _maskAge[i] = 0; }
            _maskAge[i]++;
        }
    }

    private readonly float[] _bandMask = new float[VisualFrame.BandCount];
    private readonly int[] _maskAge = new int[VisualFrame.BandCount];

    private const float RiseFloor = 0.05f;

    /// <summary>Fenetres de maintien du masque avant qu'il ne commence a decroitre.</summary>
    private const int MaskHold = 1;

    /// <summary>
    /// Decroissance du masque, par fenetre.
    ///
    /// J'ai d'abord voulu « adapter » le 0,7 de <c>bonk~</c> a nos fenetres huit fois plus
    /// longues, en le remontant a 0,94 pour garder la meme constante de temps. C'etait
    /// prendre le probleme a l'envers, et la mesure l'a dit aussitot : <b>5 charleys
    /// detectes sur cent secondes</b> au lieu de trois cents.
    ///
    /// La raison est que le masque n'est pas la pour lisser mais pour <b>s'effacer entre
    /// deux frappes</b>. S'il tient encore quand la suivante arrive, une frappe reguliere
    /// de meme amplitude ne produit aucun rapport et devient invisible. Il doit donc
    /// s'effondrer en moins d'une croche — soit precisement ce que fait le 0,7 d'origine.
    /// </summary>
    private const float MaskDecay = 0.7f;

    /// <summary>
    /// Jusqu'ou monte le registre pris en compte pour les attaques : les cinq premieres
    /// bandes, soit environ 30 a 400 Hz. Le kick et le bas de la caisse claire.
    /// </summary>
    // Au-dela de quoi un ecart de profil harmonique compte comme un changement d'accord
    // dans le vote du temps fort.
    //
    // MESURE, PAS DEVINE. A 0,45 — la valeur que la formule appelait « raisonnable » —
    // la sonde comptait 935 changements d'accord en deux minutes, soit huit par seconde :
    // ce n'etait plus un indice rare, c'etait un vote uniforme pour tous les temps, donc
    // aucune information. La distribution reelle sur un set enregistre donne p50 = 0,27
    // et p99 = 0,77. Le seuil doit vivre dans la queue de cette distribution, pas au
    // milieu — un indice qui se produit tout le temps ne discrimine rien.
    private const float ChordChangeVote = 0.75f;

    private const int KickBandLimit = 5;

    /// <summary>
    /// Vitesse a laquelle le maximum d'une bande redescend, par fenetre. A 0,999 sur
    /// 47 fenetres par seconde, il faut une quinzaine de secondes pour oublier un pic :
    /// assez lent pour ne pas pomper au rythme de la musique, assez rapide pour suivre
    /// un changement de morceau.
    /// </summary>
    private const float PeakDecay = 0.999f;

    /// <summary>Plancher, pour que le silence ne soit pas amplifie en bruit plein ecran.</summary>
    private const float MinReference = 4f;

    private static float Clamp01(float x) => x < 0f ? 0f : x > 1f ? 1f : x;
}
