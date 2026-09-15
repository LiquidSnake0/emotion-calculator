using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Emotion.Signal;

/// <summary>
/// Le message envoye a l'unite de rendu, un par fenetre d'analyse.
///
/// <b>Disposition fixe, explicite, et sans reference.</b> Chaque choix ici sert la
/// latence, et aucun n'est gratuit :
///
/// <list type="bullet">
/// <item><see cref="StructLayout"/> explicite : l'ordre des champs en memoire est celui
/// ecrit ici, et non celui que le compilateur trouverait commode. Un lecteur ecrit en
/// CUDA peut donc mapper la meme structure sans negocier.</item>
/// <item>Champs de taille fixe, tableaux inclus : la structure entiere s'ecrit par un
/// seul <c>MemoryMarshal.Write</c>, sans parcourir quoi que ce soit et sans allouer.</item>
/// <item>Aucun type reference : elle peut vivre dans de la memoire partagee entre deux
/// processus, ce qu'un <c>float[]</c> interdirait — un tableau gere est une reference
/// dans le tas d'un processus, invisible depuis l'autre.</item>
/// <item>Petits entiers plutot que des booleens separes : les attaques tiennent dans un
/// seul octet de drapeaux.</item>
/// </list>
///
/// Le tout fait 96 octets. A 47 messages par seconde, cela represente 4,5 Ko/s : la
/// bande passante n'est pas le sujet, la <b>latence</b> l'est, et une structure plate se
/// lit d'un bloc de l'autre cote.
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = Size)]
public struct GpuPacket
{
    /// <summary>
    /// Taille exacte du message, en octets. Le lecteur CUDA s'aligne dessus.
    ///
    /// <b>Quatre lignes de cache exactement.</b> Le paquet est passe de 128 a 256 octets
    /// pour que chaque source dispose de son propre mot (voir <see cref="SourceOffset"/>)
    /// au lieu de partager des octets avec ses voisines.
    /// </summary>
    public const int Size = 256;

    /// <summary>Nombre magique, pour qu'un lecteur detecte tout de suite un flux mal cadre.</summary>
    public const uint MagicValue = 0x454D5531;   // "EMU1"

    [FieldOffset(0)] public uint Magic;

    /// <summary>Numero de sequence. Un saut signale des messages perdus, ce qui est permis.</summary>
    [FieldOffset(4)] public uint Sequence;

    /// <summary>Millisecondes depuis le demarrage de la source.</summary>
    [FieldOffset(8)] public long TimeMs;

    /// <summary>Niveau global percu, 0 a 1.</summary>
    [FieldOffset(16)] public float Level;

    /// <summary>Tempo estime, ou zero tant qu'il n'est pas accroche.</summary>
    [FieldOffset(20)] public float Bpm;

    /// <summary>Position dans la mesure, 0 a 1, ou zero si le tempo n'est pas accroche.</summary>
    [FieldOffset(24)] public float Phase;

    /// <summary>Part du morceau prepare deja passee dans le master, 0 a 1.</summary>
    [FieldOffset(28)] public float Blend;

    /// <summary>Caractere tonal, 0 bruite, 1 franchement tonal.</summary>
    [FieldOffset(32)] public float Tonality;

    /// <summary>Changement d'accord, impulsion 0 a 1.</summary>
    [FieldOffset(36)] public float ChordChange;

    /// <summary>Bit 0 kick, bit 1 clap, bit 2 hat, <b>bit 3 nouveaute</b>.</summary>
    [FieldOffset(40)] public byte Hits;

    /// <summary>Classe de hauteur dominante, 0 a 11, ou 255 si aucune.</summary>
    [FieldOffset(41)] public byte Pitch;

    /// <summary>Phenomene courant, valeur de <see cref="Kind"/>.</summary>
    [FieldOffset(42)] public byte Scene;

    /// <summary>Intensite du phenomene, 0 a 255 pour 0 a 1.</summary>
    [FieldOffset(43)] public byte Intensity;

    /// <summary>Ecart a la texture des dernieres secondes, 0 a 255 pour 0 a 1.</summary>
    [FieldOffset(47)] public byte Novelty;

    /// <summary>Couleur de famille, un octet par canal.</summary>
    [FieldOffset(44)] public byte R;
    [FieldOffset(45)] public byte G;
    [FieldOffset(46)] public byte B;


    /// <summary>
    /// Les douze bandes, grave a aigu, chacune 0 a 255 pour 0 a 1. Douze octets, de 48 a 59.
    ///
    /// ELLES ETAIENT DOUZE FLOTTANTS, ET PERSONNE NE LES LISAIT COMME TELS : la fenetre
    /// lisait douze octets a 48 depuis toujours, et affichait donc l'octet bas d'un
    /// flottant. Le paquet etait par ailleurs plein a l'octet pres, et deux champs se
    /// chevauchaient a 215 (l'accord de gamme ecrasait la nettete d'evenement). Les bandes
    /// en octets rendent 36 octets — <see cref="RelaisOffset"/>, <see cref="AccordGammeOffset"/>,
    /// et de la place pour la suite — et un octet suffit a un niveau qui pilote un visuel.
    /// </summary>
    [FieldOffset(48)] public Bands12 Bands;

    /// <summary>
    /// Niveaux tonals par registre : grave, medium, aigu. 0 a 255 pour 0 a 1.
    /// Ce sont les instruments qui ne frappent pas — piano, xylophone, voix.
    /// </summary>
    [FieldOffset(96)] public byte VoiceLow;
    [FieldOffset(97)] public byte VoiceMid;
    [FieldOffset(98)] public byte VoiceHigh;

    /// <summary>Bit 0 grave, bit 1 medium, bit 2 aigu : une note vient d'etre jouee.</summary>
    [FieldOffset(99)] public byte VoiceHits;

    /// <summary>Brillance percue, 0 sourd, 255 clair.</summary>
    [FieldOffset(100)] public byte Centroid;

    /// <summary>
    /// Ouverture du filtre, 0 ferme, 255 grand ouvert. C'est le geste du DJ le plus
    /// visible et le plus frequent.
    /// </summary>
    [FieldOffset(101)] public byte Openness;

    /// <summary>Densite d'evenements, 0 vide, 255 dense.</summary>
    [FieldOffset(102)] public byte Density;

    /// <summary>
    /// Rang du temps dans la mesure, 0 a 3, ou 255 tant que le temps fort est incertain.
    /// Zero est le temps fort. Le renderer doit traiter 255 comme « je ne sais pas » et
    /// non comme une valeur : caler une structure sur un temps invente se voit
    /// immediatement.
    /// </summary>
    [FieldOffset(103)] public byte Beat;

    /// <summary>Rang de la mesure dans la phrase, 0 a 7.</summary>
    [FieldOffset(104)] public byte Bar;

    /// <summary>
    /// Position continue dans la phrase. C'est la seule grandeur du paquet qui permette
    /// d'anticiper : a 240, la phrase se termine, quoi qu'il arrive dans le son.
    /// </summary>
    [FieldOffset(105)] public byte PhrasePos;

    /// <summary>Tension qui monte, 0 a 255.</summary>
    [FieldOffset(106)] public byte Buildup;

    /// <summary>
    /// Drapeaux de structure et de geste, en un octet.
    ///
    /// Structure : 1 rupture, 2 debut de mesure, 4 debut de phrase.
    /// Gestes : 8 filtre ferme, 16 basse coupee, 32 passage dense.
    ///
    /// Les gestes sont des etats a hysteresis, pas des seuils bruts : entre les deux
    /// bornes, rien ne bascule. Un bit qui changerait plusieurs fois par seconde ferait
    /// clignoter le motif qu'il commande.
    /// </summary>
    [FieldOffset(107)] public byte StructureBits;

    /// <summary>Longueur de phrase mesuree, en mesures.</summary>
    [FieldOffset(108)] public byte PhraseBars;

    /// <summary>
    /// Mesures restantes avant la prochaine frontiere de phrase. La seule grandeur du
    /// paquet qui regarde devant — mais elle ne vaut que si <see cref="SectionSure"/> est
    /// franc, sans quoi le renderer anticiperait une frontiere inventee.
    /// </summary>
    [FieldOffset(109)] public byte BarsToBoundary;

    /// <summary>Fiabilite de la structure longue, 0 a 255.</summary>
    [FieldOffset(110)] public byte SectionSure;

    /// <summary>
    /// A quel point le renderer peut se fier a la structure, 0 a 255. Un disque qui saute,
    /// un calage en cours, un blanc entre deux disques la font tomber.
    ///
    /// <b>Elle ne coupe rien.</b> L'analyse continue et le flux ne s'interrompt pas : le
    /// renderer decide seul de ce qu'il ignore. Lui transmettre un octet coute le prix
    /// d'un octet ; lui faire redecouvrir la meme chose couterait le retard de toute la
    /// chaine, ce qu'on passe le projet a supprimer.
    /// </summary>
    [FieldOffset(111)] public byte Trust;

    /// <summary>
    /// Cotes du polygone de la voix, tires du Camelot par la fiche. Le renderer les
    /// <b>lit</b> et ne les recalcule pas : la regle vivait en double et les deux
    /// implementations avaient deja diverge.
    /// </summary>
    /// <summary>
    /// Position dans le <b>temps</b> courant, 0 a 255 pour 0 a 1. Toujours remplie.
    ///
    /// A ne pas confondre avec <see cref="Phase"/>, qui est la position dans la mesure de
    /// quatre temps et qui vaut zero tant que le « 1 » n'est pas identifie — sur un
    /// passage du bac, quatorze pour cent des images seulement.
    ///
    /// UN OCTET SUFFIT, ET CE N'EST PAS UNE ECONOMIE DE BOUT DE CHANDELLE. Il donne un
    /// deux-cent-cinquante-sixieme de temps, soit 2,7 ms a 88 BPM — huit fois plus fin que
    /// le pas d'analyse de 21 ms qui le produit. Une precision plus grande decrirait un
    /// mouvement que personne n'a mesure.
    /// </summary>
    [FieldOffset(117)] public byte BeatPhase;

    /// <summary>
    /// A quel point le temps fort est etabli, 0 a 255. C'est <see cref="BeatGrid.Confidence"/>.
    ///
    /// Elle accompagne <see cref="BeatPhase"/> et ne s'en separe pas : une phase sans son
    /// degre de certitude oblige le rendu a en inventer un, et il l'inventerait a partir
    /// des memes frappes bruitees qu'on cherche justement a ne plus lui faire lire.
    /// </summary>
    [FieldOffset(118)] public byte GridSure;

    /// <summary>
    /// A quel point une voie <b>independante</b> confirme le tempo, 0 a 255.
    /// C'est <see cref="Emotion.Signal.GridAgreement.Accord"/>.
    ///
    /// ELLE NE DIT PAS LA MEME CHOSE QUE <see cref="GridSure"/>, ET LES CONFONDRE COUTE
    /// LA PREDICTION. GridSure dit si l'on sait <i>quel</i> temps est le « 1 » ; celle-ci
    /// dit si la <i>periode</i> est la bonne. Anticiper un kick ne demande que la seconde
    /// — on n'a pas besoin de savoir ou l'on est dans la mesure pour savoir quand le temps
    /// suivant tombe. Verrouiller la prediction sur GridSure la laissait inerte : mesure
    /// sur un morceau du bac, sa mediane vaut 0,23 et elle ne passe jamais 0,45, alors que
    /// la phase publiee tourne a 87,0 temps par minute pour un tempo de 87,3.
    ///
    /// Et c'est bien la confiance a employer, parce qu'elle vient d'ailleurs : les
    /// familles de frappes se forment sur le timbre sans jamais consulter le tempo. Une
    /// confiance calculee par celui qu'on veut verifier ne verifie rien.
    /// </summary>
    [FieldOffset(119)] public byte GridAgreement;

    /// <summary>
    /// Combien de sources la separation publie EN CE MOMENT, de 0 a <see cref="SourceSlots"/>.
    ///
    /// C'est le renderer qui en a besoin : il affichait six cases quoi qu'il arrive, et
    /// quatre d'entre elles montraient du bruit avec la meme conviction que les deux vraies.
    /// « Des fois on en a 2, des fois 8, c'est justement ce que le programme est cense me
    /// dire. » Zero tant que rien n'a ete appris.
    /// </summary>
    public const int SourceActivesOffset = 120;
    [FieldOffset(SourceActivesOffset)] public byte SourceActives;

    [FieldOffset(112)] public byte Sides;

    /// <summary>Mode mineur, tire de la lettre Camelot. Non nul si mineur.</summary>
    [FieldOffset(113)] public byte Minor;

    /// <summary>
    /// Ou joue chaque registre a l'interieur du sien, 0 en bas, 255 en haut.
    ///
    /// C'est la seule grandeur du paquet qui decrive un <b>mouvement</b> plutot qu'un
    /// etat. Trois amplitudes ne disent pas qu'une melodie monte : elles disent que le
    /// medium baisse et que l'aigu monte, deux faits independants dont aucun ne porte le
    /// geste. Une position, elle, se deplace — et une forme peut la suivre.
    /// </summary>
    [FieldOffset(114)] public byte LowPitch;
    [FieldOffset(115)] public byte MidPitch;
    [FieldOffset(116)] public byte HighPitch;

    /// <summary>
    /// Ou commence la zone des sources, et combien d'octets chacune occupe.
    ///
    /// POURQUOI QUATRE OCTETS ALIGNES ET NON DES CHAMPS COTE A COTE.
    ///
    /// La disposition precedente donnait a chaque source un octet de niveau, puis rangeait
    /// les six attaques dans <i>un seul octet commun</i>. Tant qu'un seul fil ecrivait, cela
    /// tenait. Des que les six ecrivent en meme temps, cet octet commun devient le seul
    /// point de la structure ou deux sources se rencontrent — et poser un bit dans un octet
    /// se fait en le lisant, en le modifiant, en le reecrivant : deux sources qui le font
    /// ensemble s'effacent l'une l'autre, et une attaque disparait sans que rien ne le
    /// signale.
    ///
    /// Chaque source a donc maintenant <b>son mot de quatre octets, aligne</b> : son niveau,
    /// son contour, ses drapeaux, une reserve. Aucune ne partage un octet avec une autre, ce
    /// qui rend l'ecriture concurrente sure <i>par la forme des donnees</i> et non par un
    /// verrou. C'est la meme idee qu'ailleurs dans le projet : separer plutot qu'arbitrer.
    ///
    /// Huit emplacements sont reserves pour six sources. Le format n'aura pas a bouger le
    /// jour ou la separation en distinguera davantage.
    /// </summary>
    public const int SourceOffset = 128;

    /// <summary>
    /// Huit octets par source. Ce que la source sait d'elle-meme voyage avec ce qu'elle
    /// fait a l'instant : niveau, contour, drapeaux, puis empreinte, confiance et
    /// etiquette.
    ///
    /// LES QUATRE DERNIERS OCTETS N'ARRIVENT PAS EN MEME TEMPS QUE LES TROIS PREMIERS, ET
    /// C'EST VOULU.
    ///
    /// Le niveau est juste des la premiere image. L'empreinte, elle, se forme sur quelques
    /// secondes de jeu effectif — et une source qui n'entre qu'au refrain mettra une
    /// minute. Faire attendre le paquet que la plus lente soit prete gelerait tout
    /// l'affichage pour une seule forme.
    ///
    /// Le paquet part donc a cadence fixe, et chaque source y ecrit <b>ce qu'elle est en
    /// mesure d'affirmer a cet instant</b>. La confiance dit au renderer combien de credit
    /// accorder au reste : a zero, c'est une forme anonyme qui bouge ; a plein, c'est une
    /// source reconnue, et l'etiquette peut porter un nom venu de la fiche.
    /// </summary>
    public const int SourceStride = 8;
    public const int SourceSlots = 8;

    /// <summary>Rangs des octets a l'interieur du mot d'une source.</summary>
    public const int SourceLevel = 0;
    public const int SourcePitch = 1;
    public const int SourceFlags = 2;
    public const int SourceLabel = 3;
    public const int SourceHeard = 4;
    public const int SourceSharp = 5;
    public const int SourceBrightness = 6;

    /// <summary>
    /// La forme a donner a cette source. <b>Elle vient de la fiche, pas de l'analyse.</b>
    ///
    /// L'analyse sait separer et decrire ; elle ne sait pas laquelle des six merite une
    /// bouche. Ce choix est musical et appartient au DJ : sur un morceau feutre c'est la
    /// voix qu'on veut voir respirer, sur un morceau dense c'est la frappe. Le paquet le
    /// transporte tel quel jusqu'a l'unite de rendu, qui n'a plus qu'a le lire.
    /// </summary>
    public const int SourceShape = 7;

    /// <summary>
    /// Combien de cases de source le paquet ecrit : les huit, partagees entre le disque qui
    /// joue et celui qui entre pendant un fondu.
    /// </summary>
    public const int SourceCount = 8;

    /// <summary>Drapeau d'attaque, dans l'octet de drapeaux d'une source.</summary>
    public const byte SourceHitBit = 1;

    /// <summary>Les bits 1 a 3 du drapeau : la dominance de la source, de 0 a 7.</summary>
    public const int SourceDominanceShift = 1;

    /// <summary>
    /// Les bits 4 et 5 du drapeau : LA PLATINE. 1 ou 2, l'identite du disque tant qu'il
    /// tourne ; 3 le reste partage pendant un fondu ; 0 inconnu. C'est ce qui permet a
    /// l'image de suivre le fader — « le tchak cote A et la basse cote B qui tournent en
    /// meme temps » — SANS que les cases sautent de cote au relais : « celui qui joue » et
    /// « celui qui entre » changent de sens au retrait, une platine non. L'octet
    /// <see cref="RelaisOffset"/> dit laquelle joue.
    /// </summary>
    public const int SourcePlatineShift = 4;

    /// <summary>
    /// L'ETAT DU RELAIS, a 212 : bits 0–1 la platine qui joue (1 ou 2, 0 si aucune), bit 2
    /// un fondu est en cours (deux portraits suivis), bits 4–5 la platine qui entre (0 si
    /// aucune). Le rendu compose une scene par platine et lit ici de quel cote va le fader.
    /// (Il a d'abord ete pose sur un octet « libre » qui ne l'etait pas — au milieu des
    /// bandes, alors douze flottants de 48 a 95 : un octet libre se verifie sur la taille des
    /// champs, pas sur leur nombre. D'ou le passage des bandes en octets.)
    /// </summary>
    public const int RelaisOffset = 60;
    [FieldOffset(RelaisOffset)] public byte Relais;
    public const byte RelaisFonduBit = 4;

    /// <summary>
    /// LE PITCH, a 121 : le tempo mesure au master rapporte a celui que le cue a appris, en
    /// quarts de pour cent signes (-127 … +127, soit ±32 %). Zero tant qu'aucun relais n'a eu
    /// lieu ou que le DJ n'a pas touche au fader. C'est la variance que le master mesure, la
    /// seule chose qu'il cherche encore une fois les regles recues.
    /// </summary>
    public const int PitchOffset = 121;
    [FieldOffset(PitchOffset)] public sbyte PitchRelais;

    /// <summary>Les motifs des sources 6 et 7, a 122 et 124 : la suite des six mots de 243.</summary>
    [FieldOffset(122)] public ushort MotifSource6;
    [FieldOffset(124)] public ushort MotifSource7;

    /// <summary>Caracteres (126) et degres (127) des sources 6 et 7, un quartet chacune.</summary>
    [FieldOffset(126)] public byte Caracteres3;
    [FieldOffset(127)] public byte Degres3;

    /// <summary>Dans l'octet du verrou : bit 0 les sonorites sont sues, bit 1 le rythme est su.</summary>
    public const byte VerrouSourcesBit = 1;
    public const byte VerrouRythmeBit = 2;

    /// <summary>La zone des sources, vue comme des octets bruts.</summary>
    [FieldOffset(SourceOffset)] public SourceBlock Sources;

    /// <summary>
    /// Le tempo apporte par la fiche du cue, ou zero si le disque n'a pas ete prepare.
    ///
    /// Il n'est pas la pour remplacer <see cref="Bpm"/> mais pour lui etre confronte : le
    /// renderer voit d'un coup ce qu'on croyait savoir et ce que le disque fait vraiment.
    /// </summary>
    [FieldOffset(192)] public float BpmExpected;

    /// <summary>
    /// Decalage accumule entre la grille attendue et la grille reelle, en fractions de
    /// temps. Signe : negatif quand le disque traine sur la fiche.
    ///
    /// C'est la grandeur qui rend une derive perceptible. Un ecart de 1 BPM sur 87 parait
    /// negligeable ; le meme ecart deplace la grille d'un cinquieme de temps en seize
    /// temps, et d'un temps entier en une minute.
    /// </summary>
    [FieldOffset(196)] public float TempoDrift;

    /// <summary>A quel point la derive se voit, 0 a 255. Le renderer lit ceci, pas le calcul.</summary>
    [FieldOffset(200)] public byte DriftVisible;

    /// <summary>
    /// Le tempo tel qu'il vient d'etre annonce — « on est a 87,9 », puis « 88,1 ».
    ///
    /// C'est une valeur qui tient entre deux annonces, la ou <see cref="Bpm"/> tremble
    /// d'une fenetre a l'autre. Un renderer qui affiche un chiffre lit celle-ci.
    /// </summary>
    [FieldOffset(204)] public float BpmAnnounced;

    /// <summary>
    /// Une annonce vient d'etre faite sur cette image. Ne dure qu'une image : c'est un
    /// instant, pas un etat.
    /// </summary>
    [FieldOffset(208)] public byte TempoAnnounce;

    /// <summary>
    /// Famille de la frappe qui vient de tomber, ou 255 si aucune.
    ///
    /// Deux frappes de meme famille sont le meme instrument. Le renderer peut donc donner a
    /// chacune son traitement sans qu'on ait eu besoin de nommer quoi que ce soit — c'est la
    /// meme discipline que pour les six sources separees.
    /// </summary>
    /// <summary>
    /// L'ENVELOPPE DE CHAQUE SOURCE : deux octets, hors du mot de la source.
    ///
    /// Le mot d'une source fait huit octets et ils sont tous pris. Ces deux-ci vivent donc
    /// a part, dans un second bloc — ce qui ne change rien a la garantie qui comptait :
    /// deux sources n'ecrivent jamais dans le meme octet, ici comme la-bas.
    ///
    ///   +0  pique   a quel point la source monte d'un coup
    ///   +1  tenue   a quel point elle reste au niveau atteint
    ///
    /// Voir <see cref="SourceEnvelope"/>. Ce sont des DESCRIPTEURS et non des evenements :
    /// ils se moyennent sur une seconde et demie, se transportent comme le niveau, et
    /// s'interpolent comme lui. Le drapeau de frappe, lui, reste brut.
    /// </summary>
    public const int EnvelopeOffset = 216;

    /// <summary>Deux octets par source.</summary>
    public const int EnvelopeStride = 2;

    public const int EnvelopePique = 0;
    public const int EnvelopeTenue = 1;

    [FieldOffset(EnvelopeOffset)] public EnvelopeBlock Envelopes;

    /// <summary>
    /// DEPUIS COMBIEN DE TEMPS CHAQUE SOURCE S'EST TUE : un octet, en seiziemes de seconde.
    /// Zero si elle joue. Vient de <see cref="SourceEnvelope"/>.
    ///
    /// « Quand le kick est en retrait pendant un moment, on est cense le savoir. » Une
    /// source absente ne disparait pas de l'ecran : elle s'y montre eteinte, avec le temps
    /// qu'elle a passe a se taire.
    ///
    /// IL Y A EU TROIS OCTETS ICI, ET DEUX ONT ETE RETIRES. Ils portaient le role de la
    /// source dans l'orchestre — metronome, ponctuelle, continue — et sa place dans le
    /// temps. Mesure sur six morceaux du bac, la classification rendait trente-six fois
    /// « continu » sur trente-six : les montees des sources separees ne sont pas calees sur
    /// le temps, R valant 0,05 a 0,17 pour un hasard de 0,13 a 0,25. Ce n'etait pas un
    /// probleme de seuil mais de signal. Voir le README.
    /// </summary>
    public const int RetraitOffset = 232;

    /// <summary>Un octet par source.</summary>
    public const int RetraitStride = 1;

    /// <summary>Un seizieme de seconde par cran : seize secondes tiennent dans un octet.</summary>
    public const float RetraitStep = 1f / 16f;

    [FieldOffset(RetraitOffset)] public RetraitBlock Retraits;

    /// <summary>
    /// CE QUI SE REPETE, ET TOUS LES COMBIEN. Trois octets. Voir <see cref="MotifTracker"/>.
    ///
    ///   240  periode    en mesures, 0 si rien n'est designe
    ///   241  certitude  0 a 255
    ///   242  bande      quelle bande porte le motif, 0 a 11
    ///
    /// Une periode nulle veut dire « on ne sait pas », et c'est une reponse. Ce projet a
    /// deja paye une valeur par defaut qui avait l'air juste : un suivi rendait « huit
    /// mesures, cent pour cent du temps » sans avoir rien decide.
    /// </summary>
    /// <summary>
    /// Le motif de chaque source, six mots de seize bits a partir de 243 : un bit par case
    /// de la mesure (seize par mesure, la double croche), a un la ou la source monte
    /// franchement. C'est le morse A VENIR : le renderer peut allumer une case avant que
    /// le son n'arrive, et n'a plus a subir le retard de la fenetre.
    /// </summary>
    public const int MotifSourcesOffset = 243;
    [FieldOffset(MotifSourcesOffset)] public ushort MotifSource0;
    [FieldOffset(MotifSourcesOffset + 2)] public ushort MotifSource1;
    [FieldOffset(MotifSourcesOffset + 4)] public ushort MotifSource2;
    [FieldOffset(MotifSourcesOffset + 6)] public ushort MotifSource3;
    [FieldOffset(MotifSourcesOffset + 8)] public ushort MotifSource4;
    [FieldOffset(MotifSourcesOffset + 10)] public ushort MotifSource5;

    /// <summary>
    /// Le caractere de chaque source, un quartet par source a partir de 201 : 0 frappe, 15
    /// tient, mesure sur la duree. Source 0 dans le quartet bas de 201, source 1 dans le
    /// haut, et ainsi de suite jusqu'a la source 5 dans le haut de 203.
    /// </summary>
    public const int CaracteresOffset = 201;
    [FieldOffset(CaracteresOffset)] public byte Caracteres0;
    [FieldOffset(CaracteresOffset + 1)] public byte Caracteres1;
    [FieldOffset(CaracteresOffset + 2)] public byte Caracteres2;

    /// <summary>
    /// Le degre que joue chaque source dans la gamme de la fiche, un quartet par source a
    /// partir de 209 (meme rangement que les caracteres) : 0 tonique … 6, 7 hors gamme, 15
    /// inconnu. Et a 215, l'accord du chromagramme avec la gamme, 0 a 255 : bas, la fiche
    /// est contredite.
    /// </summary>
    public const int DegresOffset = 209;
    [FieldOffset(DegresOffset)] public byte Degres0;
    [FieldOffset(DegresOffset + 1)] public byte Degres1;
    [FieldOffset(DegresOffset + 2)] public byte Degres2;
    /// <summary>
    /// A 61 : il etait a 215, sur le meme octet que <see cref="EventSharp"/>, et l'ecrasait —
    /// une collision decouverte en cherchant un octet libre pour le relais.
    /// </summary>
    public const int AccordGammeOffset = 61;
    [FieldOffset(AccordGammeOffset)] public byte AccordGamme;

    /// <summary>Le verrou : 1 quand le morceau est su et que le moteur ne retouche plus.</summary>
    public const int VerrouOffset = 255;
    [FieldOffset(VerrouOffset)] public byte Verrou;

    [FieldOffset(240)] public byte MotifPeriode;

    [FieldOffset(241)] public byte MotifCertitude;

    [FieldOffset(242)] public byte MotifBande;

    [FieldOffset(212)] public byte EventFamily;

    /// <summary>L'empreinte de cette frappe, un octet par axe.</summary>
    [FieldOffset(213)] public byte EventBright;
    [FieldOffset(214)] public byte EventSpread;
    [FieldOffset(215)] public byte EventSharp;

    /// <summary>Aucune famille : la frappe n'a pas encore ete identifiee.</summary>
    public const byte NoFamily = 255;

    public const byte KickBit = 1;
    public const byte ClapBit = 2;
    public const byte HatBit = 4;

    /// <summary>Un element est entre : voix, sample, nappe. Aucun des trois autres ne le voit.</summary>
    public const byte NoveltyBit = 8;

    /// <summary>Aucune hauteur dominante.</summary>
    public const byte NoPitch = 255;

    /// <summary>
    /// Douze flottants places en ligne dans la structure, sans indirection. Un
    /// <c>float[]</c> aurait ete une reference : invisible depuis un autre processus et
    /// alloue a chaque message.
    /// </summary>
    [InlineArray(12)]
    public struct Bands12
    {
        private byte _first;
    }

    /// <summary>
    /// Compose un message a partir d'une image du signal et du morceau projete.
    /// Aucune allocation : tout est recopie dans la structure.
    /// </summary>
    /// <summary>Valeur de <see cref="Beat"/> quand le temps fort n'est pas etabli.</summary>
    public const byte NoBeat = 255;

    public const byte DropBit = 1;
    public const byte BarBit = 2;
    public const byte PhraseBit = 4;

    public const byte FilterClosedBit = 8;
    public const byte BassCutBit = 16;
    public const byte DenseBit = 32;

    private static byte Byte255(float v) => (byte)Math.Clamp(v * 255f, 0f, 255f);

    /// <summary>
    /// Ce qu'une source dit d'elle-meme, dans son mot a elle.
    /// </summary>
    /// <param name="Level">activation, 0 a 255.</param>
    /// <param name="Pitch">ou elle joue dans son registre, 0 en bas, 255 en haut.</param>
    /// <param name="Hit">une attaque vient d'etre constatee.</param>
    /// <param name="Label">nom attribue de l'exterieur, 0 tant que la source est anonyme.</param>
    /// <param name="Heard">a-t-on assez ecoute cette source. Une question de duree.</param>
    /// <param name="Sharp">la bande porte-t-elle un seul timbre. Une propriete du disque.</param>
    /// <param name="Brightness">brillance moyenne de la source.</param>
    /// <param name="Shape">la forme voulue par la fiche.</param>
    public readonly record struct SourceState(
        byte Level, byte Pitch, bool Hit,
        byte Label = 0, byte Heard = 0, byte Sharp = 0,
        byte Brightness = 128, byte Shape = 0);

    /// <summary>
    /// Ecrit ce qu'une source a a dire, dans les quatre octets qui n'appartiennent qu'a
    /// elle. Deux sources differentes peuvent appeler cette methode en meme temps sans
    /// precaution : leurs mots ne se touchent pas.
    /// </summary>
    public void WriteSource(int rank, in LaneState state, byte label = 0, byte shape = 0)
    {
        if ((uint)rank >= SourceSlots) return;

        var slot = SourceByte(rank);
        slot[SourceLevel] = Byte255(state.Level);
        slot[SourcePitch] = Byte255(state.Position);
        // Le bit 0 est la frappe ; les bits 1 a 3 portent la DOMINANCE en huit crans : ce
        // que le rendu peut marquer d'un point (extrait, sur) et ce qu'il teinte autrement
        // (discret, partage). « Le GPU saura differencier les deux si on met un point a ce
        // qui est extrait. »
        var dominance = Math.Clamp((int)(state.Dominance * 7.999f), 0, 7);
        var platine = Math.Clamp(state.Platine, 0, 3);
        slot[SourceFlags] = (byte)((state.Hit ? SourceHitBit : 0) | dominance << SourceDominanceShift | platine << SourcePlatineShift);
        slot[SourceLabel] = label;
        slot[SourceHeard] = Byte255(state.Heard);
        slot[SourceSharp] = Byte255(state.Sharpness);
        slot[SourceBrightness] = Byte255(state.Brightness);
        slot[SourceShape] = shape != 0 ? shape : Emotion.Signal.SourceShape.Default(rank);
    }

    /// <summary>Ecrit l'enveloppe d'une source : son pique et sa tenue.</summary>
    public void WriteEnvelope(int rank, float pique, float tenue)
    {
        if ((uint)rank >= SourceSlots) return;
        var slot = EnvelopeByte(rank);
        slot[EnvelopePique] = Byte255(pique);
        slot[EnvelopeTenue] = Byte255(tenue);
    }

    /// <summary>Ecrit depuis combien de secondes une source s'est tue.</summary>
    public void WriteRetrait(int rank, float retraitS)
    {
        if ((uint)rank >= SourceSlots) return;
        var all = System.Runtime.InteropServices.MemoryMarshal.CreateSpan(
            ref System.Runtime.CompilerServices.Unsafe.As<RetraitBlock, byte>(ref Retraits),
            SourceSlots * RetraitStride);
        all[rank] = (byte)Math.Clamp(retraitS / RetraitStep, 0f, 255f);
    }

    /// <summary>Relit le retrait d'une source, en octet brut.</summary>
    public byte ReadRetrait(int rank)
    {
        if ((uint)rank >= SourceSlots) return 0;
        var all = System.Runtime.InteropServices.MemoryMarshal.CreateSpan(
            ref System.Runtime.CompilerServices.Unsafe.As<RetraitBlock, byte>(ref Retraits),
            SourceSlots * RetraitStride);
        return all[rank];
    }

    /// <summary>Relit l'enveloppe d'une source, en octets bruts.</summary>
    public (byte Pique, byte Tenue) ReadEnvelope(int rank)
    {
        if ((uint)rank >= SourceSlots) return (0, 0);
        var slot = EnvelopeByte(rank);
        return (slot[EnvelopePique], slot[EnvelopeTenue]);
    }

    /// <summary>Relit ce qu'une source a ecrit.</summary>
    public SourceState ReadSource(int rank)
    {
        if ((uint)rank >= SourceSlots) return default;

        var slot = SourceByte(rank);
        return new SourceState(
            slot[SourceLevel], slot[SourcePitch], (slot[SourceFlags] & SourceHitBit) != 0,
            slot[SourceLabel], slot[SourceHeard], slot[SourceSharp],
            slot[SourceBrightness], slot[SourceShape]);
    }


    private Span<byte> EnvelopeByte(int rank)
    {
        var all = System.Runtime.InteropServices.MemoryMarshal.CreateSpan(
            ref System.Runtime.CompilerServices.Unsafe.As<EnvelopeBlock, byte>(ref Envelopes),
            SourceSlots * EnvelopeStride);
        return all.Slice(rank * EnvelopeStride, EnvelopeStride);
    }

    private Span<byte> SourceByte(int rank)
    {
        var all = System.Runtime.InteropServices.MemoryMarshal.CreateSpan(
            ref System.Runtime.CompilerServices.Unsafe.As<SourceBlock, byte>(ref Sources),
            SourceSlots * SourceStride);
        return all.Slice(rank * SourceStride, SourceStride);
    }

    public static GpuPacket From(in VisualFrame f, TrackContext track, uint sequence)
    {
        var p = new GpuPacket
        {
            Magic = MagicValue,
            Sequence = sequence,
            TimeMs = f.T,
            Level = f.Rms,
            Bpm = f.Bpm ?? 0f,
            Phase = f.Phase ?? 0f,
            Blend = f.Blend,
            Tonality = f.Harmony.Tonality,
            ChordChange = f.Harmony.Change,
            Pitch = (byte)(f.Harmony.Pitch ?? NoPitch),
            Scene = (byte)track.Scene.Kind,
            Intensity = (byte)Math.Clamp(track.Scene.Intensity * 255f, 0f, 255f),
        };

        if (f.Hits.Kick) p.Hits |= KickBit;
        if (f.Hits.Clap) p.Hits |= ClapBit;
        if (f.Hits.Hat) p.Hits |= HatBit;
        if (f.NoveltyOnset) p.Hits |= NoveltyBit;
        p.Novelty = (byte)Math.Clamp(f.Novelty * 255f, 0f, 255f);

        p.VoiceLow = (byte)Math.Clamp(f.Voices.Low * 255f, 0f, 255f);
        p.VoiceMid = (byte)Math.Clamp(f.Voices.Mid * 255f, 0f, 255f);
        p.VoiceHigh = (byte)Math.Clamp(f.Voices.High * 255f, 0f, 255f);
        if (f.Voices.LowHit) p.VoiceHits |= 1;
        if (f.Voices.MidHit) p.VoiceHits |= 2;
        if (f.Voices.HighHit) p.VoiceHits |= 4;

        p.MotifPeriode = (byte)Math.Clamp(f.MotifPeriode, 0, 255);
        p.MotifCertitude = Byte255(f.MotifCertitude);
        p.MotifBande = (byte)Math.Clamp(f.MotifBande, 0, 255);

        p.BpmExpected = f.ExpectedBpm ?? 0f;
        p.TempoDrift = f.TempoDrift;
        p.DriftVisible = (byte)Math.Clamp(f.DriftVisible * 255f, 0f, 255f);
        p.BpmAnnounced = f.AnnouncedBpm;
        p.EventFamily = f.EventFamily >= 0 ? (byte)f.EventFamily : NoFamily;
        p.EventBright = Byte255(f.EventPrint.Brillance);
        p.EventSpread = Byte255(f.EventPrint.Etalement);
        p.EventSharp = Byte255(f.EventPrint.Piquant);
        p.TempoAnnounce = f.TempoAnnounce ? (byte)1 : (byte)0;

        p.Centroid = (byte)Math.Clamp(f.Timbre.Centroid * 255f, 0f, 255f);
        p.Openness = (byte)Math.Clamp(f.Timbre.Openness * 255f, 0f, 255f);
        p.Density = (byte)Math.Clamp(f.Timbre.Density * 255f, 0f, 255f);

        // 255 signifie « temps fort inconnu », et non un rang. Le renderer doit s'abstenir
        // plutot que caler sa structure sur un temps invente.
        p.Beat = f.Structure.Beat < 0 ? NoBeat : (byte)f.Structure.Beat;
        p.Bar = (byte)f.Structure.Bar;
        p.PhrasePos = (byte)Math.Clamp(f.Structure.PhrasePos * 255f, 0f, 255f);
        p.Buildup = (byte)Math.Clamp(f.Structure.Buildup * 255f, 0f, 255f);
        if (f.Structure.Drop) p.StructureBits |= DropBit;
        if (f.Structure.BarStart) p.StructureBits |= BarBit;
        if (f.Structure.PhraseStart) p.StructureBits |= PhraseBit;
        if (f.Gestures.FilterClosed) p.StructureBits |= FilterClosedBit;
        if (f.Gestures.BassCut) p.StructureBits |= BassCutBit;
        if (f.Gestures.Dense) p.StructureBits |= DenseBit;
        p.PhraseBars = (byte)f.Structure.PhraseBars;
        p.BarsToBoundary = (byte)Math.Clamp(f.Structure.BarsToBoundary, 0, 255);
        p.SectionSure = (byte)Math.Clamp(f.Structure.SectionConfidence * 255f, 0f, 255f);
        p.Trust = (byte)Math.Clamp(f.Structure.Trust * 255f, 0f, 255f);
        p.BeatPhase = (byte)Math.Clamp(f.Structure.BeatPhase * 255f, 0f, 255f);
        p.GridSure = (byte)Math.Clamp(f.Structure.Confidence * 255f, 0f, 255f);
        p.GridAgreement = (byte)Math.Clamp(f.GridAgreement * 255f, 0f, 255f);
        p.SourceActives = (byte)Math.Clamp(f.Voices.Actives, 0, SourceSlots);
        p.Verrou = (byte)((f.Voices.Verrou ? VerrouSourcesBit : 0) | (f.Voices.VerrouRythme ? VerrouRythmeBit : 0));
        p.PitchRelais = (sbyte)Math.Clamp(MathF.Round((f.Voices.Pitch - 1f) * 400f), -127f, 127f);
        p.Relais = (byte)(Math.Clamp(f.Voices.PlatineJoue, 0, 3)
                          | (f.Voices.PlatineEntre != 0 ? RelaisFonduBit : 0)
                          | Math.Clamp(f.Voices.PlatineEntre, 0, 3) << 4);
        if (f.Voices.Caracteres is { } caracteres)
        {
            byte Q(int i) => (byte)(i < caracteres.Length ? Math.Clamp((int)MathF.Round(caracteres[i] * 15f), 0, 15) : 0);
            p.Caracteres0 = (byte)(Q(0) | Q(1) << 4);
            p.Caracteres1 = (byte)(Q(2) | Q(3) << 4);
            p.Caracteres2 = (byte)(Q(4) | Q(5) << 4);
            p.Caracteres3 = (byte)(Q(6) | Q(7) << 4);
        }
        if (f.Voices.Degres is { } degres)
        {
            byte D(int i) => (byte)(i < degres.Length ? Math.Clamp(degres[i], 0, 15) : 15);
            p.Degres0 = (byte)(D(0) | D(1) << 4);
            p.Degres1 = (byte)(D(2) | D(3) << 4);
            p.Degres2 = (byte)(D(4) | D(5) << 4);
            p.Degres3 = (byte)(D(6) | D(7) << 4);
        }
        p.AccordGamme = Byte255(f.Voices.AccordGamme);
        if (f.Voices.Motifs is { } motifs)
        {
            ushort M(int i) => i < motifs.Length ? motifs[i] : (ushort)0;
            p.MotifSource0 = M(0); p.MotifSource1 = M(1); p.MotifSource2 = M(2);
            p.MotifSource3 = M(3); p.MotifSource4 = M(4); p.MotifSource5 = M(5);
            p.MotifSource6 = M(6); p.MotifSource7 = M(7);
        }
        p.Sides = (byte)track.Sides;
        p.Minor = track.Minor ? (byte)1 : (byte)0;
        p.LowPitch = (byte)Math.Clamp(f.Voices.LowPitch * 255f, 0f, 255f);
        p.MidPitch = (byte)Math.Clamp(f.Voices.MidPitch * 255f, 0f, 255f);
        p.HighPitch = (byte)Math.Clamp(f.Voices.HighPitch * 255f, 0f, 255f);

        // Chaque source ecrit sa propre zone. Le contour part maintenant pour les six et
        // non plus pour les quatre premieres : les deux dernieres avaient un niveau qui
        // bougeait et un contour toujours nul, donc des formes qui pulsaient sur place.
        // Le nom et la forme viennent de la fiche et traversent sans etre recalcules ; le
        // reste vient de l'analyse. Les deux voyagent dans le meme mot, sans s'attendre.
        for (var i = 0; i < SourceCount; i++)
        {
            var lane = f.Voices.LaneAt(i);
            p.WriteSource(i, lane, track.NameOf(i), track.ShapeOf(i));
            p.WriteEnvelope(i, lane.Pique, lane.Tenue);
            p.WriteRetrait(i, lane.Retrait);
        }

        // Couleur deja decomposee par TrackContext : rien a analyser ici.
        var (r, g, b) = track.Rgb;
        p.R = r; p.G = g; p.B = b;

        var bands = f.Bands;
        if (bands is not null)
            for (var i = 0; i < 12 && i < bands.Length; i++)
                p.Bands[i] = Byte255(bands[i]);

        return p;
    }
}

/// <summary>
/// Les huit mots de source, cote a cote. Un bloc plat pour que le lecteur CUDA le lise
/// d'un seul acces au lieu de suivre huit champs.
/// </summary>
[System.Runtime.CompilerServices.InlineArray(GpuPacket.SourceSlots * GpuPacket.SourceStride)]
public struct SourceBlock
{
    private byte _first;
}

/// <summary>
/// Les huit enveloppes, cote a cote. Deux octets chacune, plats pour la meme raison que
/// les mots de source : un seul acces plutot que huit champs.
/// </summary>
[System.Runtime.CompilerServices.InlineArray(GpuPacket.SourceSlots * GpuPacket.EnvelopeStride)]
public struct EnvelopeBlock
{
    private byte _first;
}

/// <summary>
/// Les huit retraits, cote a cote. Un octet chacun. Les seize derniers octets du paquet
/// restent libres — ils l'etaient, deux ont ete rendus en retirant le role.
/// </summary>
[System.Runtime.CompilerServices.InlineArray(GpuPacket.SourceSlots * GpuPacket.RetraitStride)]
public struct RetraitBlock
{
    private byte _first;
}
