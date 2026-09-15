using Emotion.Signal;

// Sonde hors ligne : passe un enregistrement dans la chaine d'analyse et rend des
// chiffres.
//
// POURQUOI ELLE EXISTE. Regarder l'ecran renseigne sur ce qu'on voit, jamais sur ce qui
// decide. Chaque correction de ce projet vient d'une mesure — quatre attaques par temps,
// ecart median egal a l'ecart minimal, 22 µs par message — et aucune n'aurait ete trouvee
// a l'oeil. Un descripteur qui n'a jamais rencontre de vrai signal n'est pas un
// descripteur, c'est une intention.
//
//   dotnet run --project tools/Emotion.Probe -- <fichier.wav> [debut_s] [duree_s]

if (args.Length < 1)
{
    Console.Error.WriteLine("usage: probe <fichier.wav> [debut_s] [duree_s] [options...]");
    Console.Error.WriteLine("options : sep · inline · complexe · lisse · brutmed · median");
    Console.Error.WriteLine("          memoire · poids=X · marge=X · export=<fichier.json>");
    Console.Error.WriteLine("          relaisA=<A.wav> relaisB=<B.wav> fondu=<t0>,<t1> · paquets=<fichier.pak>");
    Console.Error.WriteLine("       probe rejoue <fichier.pak> [vitesse]   — rejoue les paquets dans l'anneau partage");
    return 1;
}

// LE REJEU : « probe rejoue <fichier.pak> ». Les paquets enregistres par « paquets= » sont
// ecrits dans l'anneau partage a leur cadence d'origine, sans serveur ni carte son. C'est
// ce qui permet de construire le rendu sur un vrai fondu enregistre — le DJ n'a pas encore
// de table — et de le rejouer a l'identique autant de fois qu'il faut.
if (args[0] == "rejoue")
{
    if (args.Length < 2) { Console.Error.WriteLine("probe rejoue <fichier.pak> [vitesse]"); return 1; }
    var vitesse = args.Length > 2 ? double.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture) : 1.0;
    var octets = File.ReadAllBytes(args[1]);
    var n = octets.Length / GpuPacket.Size;
    if (n == 0) { Console.Error.WriteLine("fichier vide"); return 1; }
    using var anneau = new SharedRingWriter();
    Console.WriteLine($"{n} paquets, {(n / 47.0):F0} s environ, vers {anneau.Path} (Ctrl-C arrete)");
    var depart = System.Diagnostics.Stopwatch.StartNew();
    long t0 = System.Runtime.InteropServices.MemoryMarshal.Read<GpuPacket>(octets.AsSpan(0, GpuPacket.Size)).TimeMs;
    for (var i = 0; i < n; i++)
    {
        var p = System.Runtime.InteropServices.MemoryMarshal.Read<GpuPacket>(octets.AsSpan(i * GpuPacket.Size, GpuPacket.Size));
        var du = (p.TimeMs - t0) / vitesse - depart.Elapsed.TotalMilliseconds;
        if (du > 1) Thread.Sleep((int)du);
        anneau.Write(in p);
    }
    Console.WriteLine("fin du rejeu");
    return 0;
}

var path = args[0];
var startS = args.Length > 1 ? double.Parse(args[1]) : 0;
var lengthS = args.Length > 2 ? double.Parse(args[2]) : 90;

// Export des images analysees, pour rejouer l'analyse sans materiel ni serveur.
//
// NOMME, ET PLUS POSITIONNEL. Il occupait le quatrieme rang, si bien qu'un drapeau passe
// la — « brut », « median » — etait pris pour un chemin de sortie et laissait un fichier
// du meme nom a la racine du depot. Tous les arguments qui suivent la duree sont
// desormais nommes.
var exportTo = args.FirstOrDefault(a => a.StartsWith("export="))?[7..];
var exported = new List<string>();

var (mono, rate) = Wav.ReadMono(path, startS, lengthS);
Console.WriteLine($"{Path.GetFileName(path)} — {mono.Length / (float)rate:F1} s a {rate} Hz");

// Cinquieme argument : « sep » force la separation harmonique/percussive, coupee par
// defaut depuis qu'on l'a mesuree. Sert a comparer les deux sur la meme matiere.
var separate = args.Contains("sep");
// « memtempo=X » et « inertie=X » : les deux leviers du temps d’accroche du tempo.
// Le nom evite « memoire », deja pris par le mode deux passages.
var memoireT = TempoTracker.DefautMemoireS; var inertieT = TempoTracker.DefautInertie;
foreach (var a in args)
{
    if (a.StartsWith("memtempo=") && float.TryParse(a[9..],
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var mv)) memoireT = mv;
    if (a.StartsWith("inertie=") && float.TryParse(a[8..],
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var iv)) inertieT = iv;
}
// « largeur=X » : largeur de la fenetre de preference du tempo, en octaves.
var largeurPref = 0.25f;
foreach (var a in args)
    if (a.StartsWith("largeur=") && float.TryParse(a[8..],
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var lp)) largeurPref = lp;
// « prefere=X » : centre de la preference de tempo. C'est par la que la fiche du crate
// entre dans l'analyse — non pour imposer une valeur, mais pour dire ou chercher.
var centrePref = 90f;
foreach (var a in args)
    if (a.StartsWith("prefere=") && float.TryParse(a[8..],
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var cp)) centrePref = cp;
var analyzer = new SpectrumAnalyzer(rate, separate, memoireT, inertieT, centrePref, largeurPref);

// LE RELAIS, HORS LIGNE : « relaisA=<A.wav> relaisB=<B.wav> fondu=<t0>,<t1> ».
//
// Le fichier analyse est alors un MELANGE : A seul, puis un fondu de A vers B entre t0 et
// t1, puis B seul. Deux analyseurs de cue apprennent A et B chacun sur leur propre fichier
// (les soixante premieres secondes, apprentissage dans le fil), et l'analyseur principal
// devient le master : il ne forme aucun portrait, il recoit celui de A avant la premiere
// image, accueille celui de B a t0 et retire A a t1 — exactement ce que DualAudioSource
// fait au rythme du fader. C'est la mesure du relais : sur chaque image, ce que chaque
// case publie et a quel disque elle dit appartenir, a confronter aux stems de A et de B.
var relaisA = args.FirstOrDefault(a => a.StartsWith("relaisA="))?[8..];
var relaisB = args.FirstOrDefault(a => a.StartsWith("relaisB="))?[8..];
double fonduT0 = -1, fonduT1 = -1;
if (args.FirstOrDefault(a => a.StartsWith("fondu="))?[6..] is { } fonduArg)
{
    var parts = fonduArg.Split(',');
    fonduT0 = double.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture);
    fonduT1 = double.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture);
}
SpectrumAnalyzer.EmpreinteDisque? empreinteB = null;
var relaisFait = 0;   // 0 rien, 1 B accueilli, 2 A retire
if (relaisA is not null && relaisB is not null)
{
    SpectrumAnalyzer.EmpreinteDisque Apprendre(string fichier)
    {
        var (son, tx) = Wav.ReadMono(fichier, 0, 60);
        var cue = new SpectrumAnalyzer(tx, separate, memoireT, inertieT, centrePref, largeurPref) { Role = RoleAnalyseur.Cue };
        cue.Separation.ApprentissageEnLigne = true;
        for (var i = 0; i + SpectrumAnalyzer.Window <= son.Length; i += SpectrumAnalyzer.Window)
            cue.Analyze(son.AsSpan(i, SpectrumAnalyzer.Window), (long)(i * 1000L / tx));
        var e = cue.Empreinte();
        Console.WriteLine($"cue {Path.GetFileName(fichier)} : {e.Sources.Actives} gabarits + reste, " +
                          $"accordage {e.Sources.AccordageCents:+0;-0} cents, tempo {e.Bpm:F1}, " +
                          $"motifs {string.Join(" ", e.StabiliteMotifs.Select(s => s.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)))}");
        return e;
    }
    var empreinteA = Apprendre(relaisA);
    empreinteB = Apprendre(relaisB);
    analyzer.Role = RoleAnalyseur.Master;
    analyzer.Accueillir(empreinteA);
    analyzer.Retirer();
    if (empreinteA.Bpm > 0f) analyzer.AdoptTempo(empreinteA.Bpm, 0);
    Console.WriteLine($"master : suit A ({analyzer.Separation.Actives} gabarits), accueil de B a {fonduT0:F0} s, retrait de A a {fonduT1:F0} s");
}

// LES DEUX REGIMES, DANS LE MEME PROCESSUS.
//
// Un ecart tenace separait la sonde du direct : sur le meme fichier, la sonde trouve 87 BPM
// d'un bout a l'autre et le serveur publie 113 pendant treize a quinze pour cent du temps.
// Le son a ete disculpe (l'enregistrement de la capture redonne 87 dans la sonde), la
// capture aussi (une source qui rejoue un fichier sans perte produit quand meme 113).
//
// Ne restaient que deux variables, et il fallait pouvoir les actionner separement :
//
//   « cadence »   attend entre deux fenetres, comme si le son arrivait en temps reel
//   « murale »    date les fenetres a l'horloge, et non au compte d'echantillons
//
// Le direct a les deux, la sonde n'en avait aucune. Les activer une par une dit laquelle
// porte le defaut — et si aucune ne le reproduit, c'est qu'il faut chercher ailleurs.
var cadenceReelle = args.Contains("cadence");
var horlogeMurale = args.Contains("murale");
var departReel = DateTime.UtcNow;

// « bpmtrace=<fichier> » : le tempo publie a chaque fenetre, pour comparer deux regimes
// image par image plutot que de comparer deux moyennes.
var bpmTrace = args.FirstOrDefault(a => a.StartsWith("bpmtrace="))?[9..];
var traceBpm = bpmTrace is null ? null : new List<string>();

// « inline » en argument : fait tourner l'apprentissage dans le fil d'analyse, comme
// avant. Sert a comparer les deux regimes sur le meme morceau.
if (args.Contains("inline")) analyzer.Separation.ApprentissageEnLigne = true;
analyzer.Etapes.Actif = true;

// « complexe » en argument : juge les attaques dans le domaine complexe plutot que sur le
// flux d'energie. Sert a comparer les deux sur la meme matiere.
if (args.Contains("complexe")) analyzer.FluxComplexeActif = true;

// « lisse » : remet la moyenne sur deux fenetres avant de juger le kick, comme avant.
// Sert a refaire la comparaison sur une autre matiere. Voir SpectrumAnalyzer.LissageKick.
if (args.Contains("lisse")) analyzer.LissageKick = true;

// « fiche=X » : le tempo annonce par le crate, par le chemin reel — celui qu'emprunte
// SpectrumAnalyzer.Reprendre quand la memoire de piste rend ce qu'elle savait du disque.
// C'est une amorce, jamais un verrou : l'autocorrelation continue de chercher.
foreach (var a2 in args)
{
    // « cle=9A » : la tonalite de la fiche du crate, en Camelot — un prior doux sur les
    // positions des gabarits, et les degres publies.
    if (a2.StartsWith("cle=")) analyzer.Gamme(a2[4..]);
    if (a2.StartsWith("fiche=") && float.TryParse(a2[6..],
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var fb))
        analyzer.Amorcer(fb);
}

// « repli=0 » : coupe le repli d'energie qui donne sa phase a la grille, pour comparer.
// « memrepli=X » : memoire du repli, en temps.
// « nosync » : les frappes ne tirent plus la grille ; seul le repli la place.
if (args.Contains("repli=0")) analyzer.ReplierPhase = false;
if (args.Contains("nosync")) analyzer.CalerSurFrappes = false;
foreach (var a5 in args)
    if (a5.StartsWith("med=") && float.TryParse(a5[4..],
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var pm))
        analyzer.PoidsMedium = pm;
foreach (var a4 in args)
    if (a4.StartsWith("demi=") && float.TryParse(a4[5..],
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var av))
        analyzer.AvantageFrappe = av;
foreach (var a3 in args)
    if (a3.StartsWith("memrepli=") && float.TryParse(a3[9..],
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var mr))
        analyzer.MemoireRepli = mr;

// « brutmed » : retire aussi le lissage du clap et du charley. Mesure sur macro : nuisible.
if (args.Contains("brutmed")) analyzer.LissageAttaques = false;

// « median » : seuil pris sur la mediane de l'historique plutot que sur sa moyenne.
if (args.Contains("median")) analyzer.SeuilMedian = true;

// « blanc=X » : part du flux blanchi dans le jugement du kick.
// « blanchz=X » : jusqu'ou il regarde, en hertz.
foreach (var a in args)
{
    if (a.StartsWith("blanc=") && float.TryParse(a[6..],
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var bw))
        analyzer.PoidsBlanchi = bw;
    if (a.StartsWith("blanchz=") && float.TryParse(a[8..],
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var bh))
        analyzer.BlanchiHz = bh;
    // « gap=X » : fraction de temps de surdite apres une frappe.
    if (a.StartsWith("gap=") && float.TryParse(a[4..],
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var gp))
        analyzer.PartDeTemps = gp;
    // « bandes=N » : derniere bande, exclue, sur laquelle le kick est juge.
    if (a.StartsWith("bandes=") && int.TryParse(a[7..], out var bd))
        analyzer.BandesKick = bd;
    // « fermete=X » : force minimale d'un kick, en fraction de celle des precedents.
    if (a.StartsWith("fermete=") && float.TryParse(a[8..],
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var fm))
        analyzer.FermeteKick = fm;
}

// « marge=X » : marge du kick au-dessus du fond, pour la balayer.
foreach (var a in args)
    if (a.StartsWith("marge=") && float.TryParse(a[6..],
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var mg))
        analyzer.MargeKick = mg;

// « poids=X » : part du domaine complexe dans le jugement du kick, pour la regler par
// la mesure plutot que de la choisir.
foreach (var a in args)
    if (a.StartsWith("poids=") && float.TryParse(a[6..],
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var w))
        analyzer.PoidsComplexe = w;

// « memoire » en argument : joue l'extrait DEUX FOIS de suite dans le meme processus, la
// seconde reprenant ce que la premiere a etabli. C'est ainsi que la reprise se produit
// vraiment — en memoire vive, sans jamais toucher au disque dur — et cela reproduit ce qui
// arrive quand on repose l'aiguille au debut pendant un calage.
var deuxPassages = args.Contains("memoire");
Console.WriteLine(separate ? "separation active" : "separation COUPEE");
const int hop = SpectrumAnalyzer.Window;

var barStarts = new List<long>();
var phraseStarts = new List<long>();
var drops = new List<long>();
var confidences = new List<float>();
var accroche = new List<(long T, float? Bpm, float Conf)>();
var buildups = new List<float>();
int kicks = 0, claps = 0, hats = 0, novelties = 0, chordChanges = 0;
int vLow = 0, vMid = 0, vHigh = 0;
var beatHisto = new int[5];   // -1 puis 0..3
var changes = new List<float>();
var slopes = new List<float>();
var tempos = new List<float>();

// Ou tombent les ruptures dans la phrase. Si la musique se construit vraiment en 4, 8,
// 16, elles doivent se concentrer sur quelques mesures et non se repartir au hasard —
// et c'est ce qui rendrait leur prochaine occurrence previsible.
var breakBar = new int[Structure.DefaultPhraseBars];
var breakBeat = new int[4];
var lastBreakBar = -1;
var breakGaps = new List<int>();
var barCounter = 0;
var freeBreaks = new List<int>();
var phraseLen = new Dictionary<int, int>();
var sectionConf = new List<float>();
var profileConf = new List<float>();
var agree = 0;
var compared = 0;
var ruptures = 0;
var kickAt = new List<long>();
var fichierChroma = args.FirstOrDefault(a => a.StartsWith("chroma="))?[7..];
double[]? chromaMoyen = fichierChroma is null ? null : new double[12];
var chromaImages = 0;
IReadOnlyList<ProfileLearner.Bilan> bilansVus = [];
using var journalSources = args.FirstOrDefault(a => a.StartsWith("sources="))?[8..] is { } fichierSources
    ? new StreamWriter(fichierSources) : null;
using var fichierPaquets = args.FirstOrDefault(a => a.StartsWith("paquets="))?[8..] is { } cheminPaquets
    ? new FileStream(cheminPaquets, FileMode.Create, FileAccess.Write) : null;
var numeroPaquet = 0;
var syncErr = new List<float>();
var offsets = new List<float>();
var gridMs = new List<float>();
var tempoMs = new List<float>();
var lastReason = "";

// Combien de temps mur coute une image d'analyse. C'est la seule mesure qui dise si la
// chaine tient le direct : une image qui depasse le pas de 21 ms fait prendre du retard,
// et ce retard s'accumule.
var coutImage = new List<double>();
var famAt = Enumerable.Range(0, EventFamilies.Max).Select(_ => new List<long>()).ToArray();

// Seize cases par mesure de quatre temps, une par double croche.
var famCase = Enumerable.Range(0, EventFamilies.Max).Select(_ => new int[16]).ToArray();
var famTotal = new int[EventFamilies.Max];
int[] kickCase = new int[16], clapCase = new int[16], hatCase = new int[16];
int kickTotalC = 0, clapTotalC = 0, hatTotalC = 0;
var phaseHisto = new int[16];
var nosTemps = new List<long>();

// LES INSTANTS DU MEDIUM ET DE L'AIGU, POUR POUVOIR LES CONFRONTER AU MEME JUGE.
//
// Seul le kick etait exporte, parce que seul le kick cale la grille. Or la mesure du
// repli d'energie dit que le registre du kick est le PIRE endroit ou chercher la phase du
// temps — replie sur le grave elle se trompe de 231 ms, sur le medium de 60. On ne peut
// pas verifier cela sans sortir aussi ce que le medium et l'aigu ont detecte.
//
// La date est celle de la fenetre : la correction de transitoire n'est calculee que pour
// le kick. Cela ne gene pas la concentration, qui est invariante par decalage — mais
// interdit d'en tirer une conclusion sur un retard.
var clapAt = new List<long>();
var hatAt = new List<long>();

// LES TEMPS QUE LE REPLI DESIGNE, INDEPENDAMMENT DE LA GRILLE.
//
// Sans cette sortie on ne peut pas savoir si un repli decevant vient du repli lui-meme ou
// de la facon dont il tire la grille — deux defauts qui se corrigent a des endroits
// opposes. On note l'instant ou sa phase repasse par zero : c'est la qu'il place le temps.
var repliAt = new List<long>();
var repliPrec = -1f;

// Combien de fois chaque source s'est declaree absente, et a quel niveau elle joue.
var absentes = new int[Voices.Registers];
var niveaux = new List<float>[Voices.Registers];
for (var i = 0; i < Voices.Registers; i++) niveaux[i] = new List<float>(4096);
var cadres = 0;
var dernierBeat = -2;
var fluxE = new List<float>();
var fluxC = new List<float>();
var annonces = new List<(long T, float Bpm)>();
var murAt = new long[Voices.Registers];
var assezAt = new long[Voices.Registers];
Array.Fill(murAt, -1L);
Array.Fill(assezAt, -1L);
var chronoImage = new System.Diagnostics.Stopwatch();

// Premier passage, quand on demande la reprise : on ecoute une fois, on garde ce qu'on a
// appris, et la boucle qui suit repart dessus.
if (deuxPassages)
{
    for (var i = 0; i + hop <= mono.Length; i += hop)
        analyzer.Analyze(mono.AsSpan(i, hop), (long)(i * 1000L / rate));

    var appris = analyzer.Connaissance(Path.GetFileNameWithoutExtension(path), default);
    Console.WriteLine($"premier passage     {appris.SecondsHeard:F0} s · " +
                      $"{appris.Sources.Sum(x => x.Observations)} observations · " +
                      $"tempo {appris.Bpm:F1}");

    analyzer.NewTrack();
    analyzer.Reprendre(appris);
}

for (var i = 0; i + hop <= mono.Length; i += hop)
{
    var tMs = (long)(i * 1000L / rate);

    // La cadence : on attend que la fenetre soit « arrivee », comme le ferait la carte son.
    if (cadenceReelle)
    {
        var retard = tMs - (DateTime.UtcNow - departReel).TotalMilliseconds;
        if (retard > 1) Thread.Sleep((int)retard);
    }

    // L'horodatage : l'horloge murale plutot que le compte d'echantillons.
    if (horlogeMurale) tMs = (long)(DateTime.UtcNow - departReel).TotalMilliseconds;

    // Le relais hors ligne, au rythme du fondu declare.
    if (empreinteB is not null)
    {
        if (relaisFait == 0 && tMs >= fonduT0 * 1000) { analyzer.Accueillir(empreinteB); relaisFait = 1;
            Console.WriteLine($"   t={tMs / 1000.0,6:F1} s  accueil de B : {analyzer.Separation.Actives} gabarits suivis, reste partage"); }
        if (relaisFait == 1 && empreinteB.Bpm > 0f && tMs >= (fonduT0 + fonduT1) * 500) { analyzer.AdoptTempo(empreinteB.Bpm, tMs); relaisFait = 3; }
        if (relaisFait is 1 or 3 && tMs >= fonduT1 * 1000) { analyzer.Retirer(); relaisFait = 2;
            Console.WriteLine($"   t={tMs / 1000.0,6:F1} s  retrait de A : {analyzer.Separation.Actives} gabarits suivis"); }
    }

    chronoImage.Restart();
    var f = analyzer.Analyze(mono.AsSpan(i, hop), tMs);
    coutImage.Add(chronoImage.Elapsed.TotalMilliseconds);

    // « paquets=<fichier.pak> » : chaque image, telle que le rendu la recevrait — les 256
    // octets de GpuPacket, bout a bout. A rejouer avec « probe rejoue ».
    if (fichierPaquets is not null)
    {
        var paquet = GpuPacket.From(in f, TrackContext.Silence, (uint)++numeroPaquet);
        var brut = System.Runtime.InteropServices.MemoryMarshal.AsBytes(
            System.Runtime.InteropServices.MemoryMarshal.CreateReadOnlySpan(ref paquet, 1));
        fichierPaquets.Write(brut);
    }

    // A quel instant chaque source devient assez sure d'elle pour qu'un nom tienne. Les
    // six ne s'attendent pas : c'est tout l'interet de les faire murir separement.
    for (var r = 0; r < Voices.Registers; r++)
    {
        if (murAt[r] < 0 && f.Voices.LaneAt(r).Confidence >= 0.6f) murAt[r] = tMs;
        // Quand la source a ete assez ecoutee — independamment de savoir si sa bande est
        // nette. Les deux sont differents et etaient confondus dans une seule grandeur.
        if (assezAt[r] < 0 && f.Voices.LaneAt(r).Heard >= 0.99f) assezAt[r] = tMs;
    }

    // « sources=<fichier> » : ce que chaque source publie, image par image — niveau, frappe,
    // pique, tenue — pour confronter son morse aux attaques reelles d'un juge exterieur.
    if (journalSources is not null && f.Voices.Actives > 0)
    {
        var ligne = new System.Text.StringBuilder();
        ligne.Append((tMs / 1000.0).ToString("F3", System.Globalization.CultureInfo.InvariantCulture));
        // La position dans la mesure, de 0 a 1 (ou -1 sans grille) : pour ranger les frappes
        // de chaque source sur les seize cases de la mesure.
        ligne.Append('\t').Append((f.Phase ?? -1f).ToString("F4", System.Globalization.CultureInfo.InvariantCulture));
        for (var r = 0; r < f.Voices.Actives; r++)
        {
            var l = f.Voices.LaneAt(r);
            // Cinq colonnes par case : niveau, frappe, pique, tenue, et LA PLATINE (1 ou 2,
            // 3 le reste partage) — c'est ce que le relais publie.
            ligne.Append('\t').Append(l.Level.ToString("F3", System.Globalization.CultureInfo.InvariantCulture))
                 .Append('\t').Append(l.Hit ? '1' : '0')
                 .Append('\t').Append(l.Pique.ToString("F3", System.Globalization.CultureInfo.InvariantCulture))
                 .Append('\t').Append(l.Tenue.ToString("F3", System.Globalization.CultureInfo.InvariantCulture))
                 .Append('\t').Append(l.Platine);
        }
        journalSources.WriteLine(ligne.ToString());
    }

    // Chaque balayage ou croissance, quand il tombe : pour lire les seuils sur un morceau.
    if (!ReferenceEquals(analyzer.Separation.Bilans, bilansVus) && analyzer.Separation.Bilans.Count > 0)
    {
        bilansVus = analyzer.Separation.Bilans;
        Console.WriteLine($"   t={tMs / 1000.0,6:F1} s  bilans  " + string.Join("  ",
            bilansVus.Select(b => $"{b.K}:{100 * b.Reste:F2}%/doublon {b.Doublon:F2}/lien {b.Lien:F2}"))
            + $"  -> {analyzer.Separation.Actives} actives");
    }

    // « chroma=<fichier> » : le chromagramme moyen du moteur sur la passe, douze classes, pour
    // confronter la tonalite qu'il entend a la fiche Camelot du crate.
    if (chromaMoyen is not null && f.Harmony.Chroma is { Length: 12 } ch)
    {
        for (var k = 0; k < 12; k++) chromaMoyen[k] += ch[k];
        chromaImages++;
    }

    if (f.TempoAnnounce) annonces.Add((tMs, f.AnnouncedBpm));
    if (f.EventFamily >= 0) famAt[f.EventFamily].Add(tMs);

    // OU DANS LA MESURE ? La question posee par le DJ : une frappe tombe-t-elle toujours
    // au meme endroit du cycle de quatre temps ? Si oui, on peut l'annoncer depuis la
    // grille au lieu de l'attendre — et l'annoncer, c'est l'allumer avant que le son
    // n'arrive.
    //
    // La position se lit sans rien ajouter a la bibliotheque : le rang du temps dans la
    // mesure, plus la phase a l'interieur du temps. Seize cases par mesure, soit la double
    // croche : plus fin que ca et la resolution d'analyse deciderait a la place du motif.
    // ATTENTION AU SENS DE `Phase` : elle vaut deja la position dans la mesure de quatre
    // temps, de 0 a 1, et non la phase a l'interieur d'un temps. La combiner avec
    // `Structure.Beat` la recomptait et rabattait toutes les frappes sur les temps forts —
    // 94 % de charleys pile sur le temps, ce qui ne pouvait pas etre vrai.
    if (f.Phase is { } ph)
    {
        phaseHisto[Math.Clamp((int)(Math.Clamp(ph, 0f, 0.999f) * 16), 0, 15)]++;
        var case16 = Math.Clamp((int)(Math.Clamp(ph, 0f, 0.999f) * 16), 0, 15);
        if (f.EventFamily >= 0) { famCase[f.EventFamily][case16]++; famTotal[f.EventFamily]++; }
        if (f.Hits.Kick)  { kickCase[case16]++; kickTotalC++; }
        if (f.Hits.Clap)  { clapCase[case16]++; clapTotalC++; }
        if (f.Hits.Hat)   { hatCase[case16]++;  hatTotalC++;  }
    }
    fluxE.Add(analyzer.DernierFluxEnergie);
    fluxC.Add(analyzer.DernierFluxComplexe);

    // Les instants de temps de NOTRE grille, pour pouvoir la confronter a une autre.
    if (f.Structure.Beat >= 0 && f.Structure.Beat != dernierBeat)
    {
        dernierBeat = f.Structure.Beat;
        nosTemps.Add(tMs);
    }

    if (f.Hits.Kick)
    {
        kicks++;
        // L'instant corrige, pas celui de la fenetre : c'est lui que la grille emploie et
        // lui qu'on confronte au dehors.
        kickAt.Add(analyzer.FrappeMs);
        syncErr.Add(MathF.Abs(analyzer.SyncError));
        offsets.Add(analyzer.TransientOffsetMs);
    }
    var phRepli = analyzer.Repli.PhaseDuTemps;
    if (analyzer.Repli.Temps >= 4f && repliPrec >= 0f && phRepli < repliPrec - 0.5f)
        repliAt.Add(f.T - (long)(phRepli * 60_000f / Math.Max(1f, analyzer.Bpm ?? 87f)));
    repliPrec = phRepli;

    cadres++;
    for (var src = 0; src < Voices.Registers; src++)
    {
        var voie = f.Voices.LaneAt(src);
        if (voie.Retrait > 0.4f) absentes[src]++;
        niveaux[src].Add(voie.Level);
    }

    if (f.Hits.Clap) { claps++; clapAt.Add(f.T); }
    if (f.Hits.Hat) { hats++; hatAt.Add(f.T); }
    if (f.Voices.LowHit) vLow++;
    if (f.Voices.MidHit) vMid++;
    if (f.Voices.HighHit) vHigh++;
    if (f.NoveltyOnset) novelties++;
    if (f.Harmony.Change > 0.45f) chordChanges++;
    changes.Add(f.Harmony.Change);
    gridMs.Add(analyzer.GridBeatMs);
    if (f.Bpm is { } bq) tempoMs.Add(60_000f / bq);
    if (f.Bpm is { } bp) tempos.Add(bp);
    traceBpm?.Add($"{i / rate} {(f.Bpm is { } bq2 ? bq2.ToString("F2", System.Globalization.CultureInfo.InvariantCulture) : "vide")}");

    if (exportTo is not null)
    {
        // Format compact : des nombres, pas des objets. Mille images nommees pesent dix
        // fois ce que pesent mille lignes de valeurs, et c'est une page web qui les lit.
        var b = string.Join(",", f.Bands.Select(v => v.ToString("F3",
            System.Globalization.CultureInfo.InvariantCulture)));
        var flags = (f.Hits.Kick ? 1 : 0) | (f.Hits.Clap ? 2 : 0) | (f.Hits.Hat ? 4 : 0)
                  | (f.Voices.LowHit ? 8 : 0) | (f.Voices.MidHit ? 16 : 0)
                  | (f.Voices.HighHit ? 32 : 0) | (f.NoveltyOnset ? 64 : 0)
                  | (f.Structure.Drop ? 128 : 0) | (f.Gestures.FilterClosed ? 256 : 0)
                  | (f.Gestures.BassCut ? 512 : 0) | (f.Gestures.Dense ? 1024 : 0);
        string N(float v) => v.ToString("F3", System.Globalization.CultureInfo.InvariantCulture);
        exported.Add($"[{tMs},{N(f.Rms)},[{b}],{flags},{N(f.Voices.Low)},{N(f.Voices.Mid)}," +
                     $"{N(f.Voices.High)},{N(f.Timbre.Openness)},{N(f.Timbre.Centroid)}," +
                     $"{N(f.Timbre.Density)},{(f.Bpm is { } bb ? N(bb) : "0")}," +
                     $"{f.Structure.Beat},{N(f.Structure.Buildup)},{N(f.Novelty)}," +
                     $"{N(f.Structure.Confidence)},{N(f.Harmony.Tonality)}," +
                     $"{N(f.Voices.LowPitch)},{N(f.Voices.MidPitch)},{N(f.Voices.HighPitch)}," +
                     "[" + string.Join(",", Enumerable.Range(0, Voices.Registers)
                            .Select(i => N(f.Voices.LevelAt(i)))) + "]," +
                     "[" + string.Join(",", Enumerable.Range(0, Voices.Registers)
                            .Select(i => N(f.Voices.PitchAt(i)))) + "]," +
                     f.Voices.Hits + "," +
                     // Deux grandeurs distinctes, et il faut les deux. « Assez ecoutee »
                     // est une question de duree et se resout en quelques secondes ;
                     // « bande nette » est une propriete du disque et ne bouge pas avec le
                     // temps. Confondues, elles donnaient une barre qui trompait.
                     "[" + string.Join(",", Enumerable.Range(0, Voices.Registers)
                            .Select(i => N(f.Voices.LaneAt(i).Heard))) + "]," +
                     "[" + string.Join(",", Enumerable.Range(0, Voices.Registers)
                            .Select(i => N(f.Voices.LaneAt(i).Sharpness))) + "]," +
                     "[" + string.Join(",", Enumerable.Range(0, Voices.Registers)
                            .Select(i => (int)f.Voices.LabelAt(i))) + "]," +
                     $"{N(f.AnnouncedBpm)},{(f.TempoAnnounce ? 1 : 0)},{N(f.DriftVisible)}]");
    }

    var s = f.Structure;
    if (s.BarStart) { barStarts.Add(tMs); barCounter++; }
    if (f.NoveltyOnset && s.Confidence > 0.35f)
    {
        breakBar[Math.Clamp(s.Bar, 0, Structure.DefaultPhraseBars - 1)]++;
        if (s.Beat >= 0) breakBeat[s.Beat]++;
        freeBreaks.Add(barCounter);
        if (lastBreakBar >= 0) breakGaps.Add(barCounter - lastBreakBar);
        lastBreakBar = barCounter;
    }
    if (s.PhraseStart) phraseStarts.Add(tMs);
    if (s.Drop) drops.Add(tMs);
    confidences.Add(s.Confidence);
    // Pour mesurer QUAND chaque grandeur se pose, et non seulement ou elle finit.
    accroche.Add((tMs, f.Bpm, s.Confidence));
    phraseLen[s.PhraseBars] = phraseLen.GetValueOrDefault(s.PhraseBars) + 1;
    sectionConf.Add(s.SectionConfidence);
    if (analyzer.ContinuityBroken) { ruptures++; lastReason = analyzer.LastBreak; }
    var (pOff, pConf, _, gBeat) = analyzer.Downbeat;
    profileConf.Add(pConf);
    // Le profil designe une position de grille ; la grille, elle, publie le rang du temps.
    // Ils sont d'accord quand le temps que le profil designe est bien le temps fort.
    if (gBeat >= 0 && pConf > 0.2f)
    {
        compared++;
        if (((int)(s.Beat) == 0) == (pOff == (int)(pOff))) { }
    }
    buildups.Add(s.Buildup);
    var (sb, sa, su) = analyzer.Slopes;
    slopes.Add(MathF.Abs(sb) + MathF.Abs(sa) + MathF.Abs(su));
    beatHisto[s.Beat + 1]++;
}

// LE RETRAIT DE CHAQUE SOURCE, PAR LA SONDE ET NON PAR LE SERVEUR.
//
// Mesurer cela demandait jusqu'ici de lancer le moteur web et d'ouvrir un port. La sonde
// fait tourner exactement le meme analyseur sans rien ouvrir : c'est elle qu'il faut
// interroger, et l'oublier a coute des ports laisses ouverts.
// LE MOTIF : quelle bande se repete, et tous les combien.
{
    var m = analyzer.Motif;
    var bande = m.Meilleure();
    Console.Write($"motif   {m.Mesures} mesures entendues   ");
    if (bande < 0)
        Console.WriteLine("aucune bande ne designe de periode");
    else
        Console.WriteLine($"bande {bande} : {m.Periode(bande)} mesures "
                          + $"(certitude {m.Certitude(bande):F2})");
    Console.Write("        par bande        ");
    for (var i = 0; i < MotifTracker.Bandes; i++)
    {
        var p = m.Periode(i);
        Console.Write(p > 0 ? $"{i}:{p} " : ". ");
    }
    Console.WriteLine();
}

Console.Write("sources, part du temps absentes  ");
for (var i = 0; i < Voices.Registers; i++)
    Console.Write($"{i + 1}:{100.0 * absentes[i] / Math.Max(1, cadres):F0}% ");
Console.WriteLine();
// LA MEDIANE NE DIT PAS SI UNE CASE S'ALLUME. Une source qui joue une fois par mesure passe
// l'essentiel du temps a zero et sa mediane vaut zero, qu'elle soit visible ou invisible. Ce
// qui decide de ce qu'on voit est le HAUT de sa distribution.
Console.Write("           mediane / q90         ");
for (var i = 0; i < Voices.Registers; i++)
{
    niveaux[i].Sort();
    var n = niveaux[i].Count;
    var med = n > 0 ? niveaux[i][n / 2] : 0f;
    var q90 = n > 0 ? niveaux[i][(int)(0.9 * (n - 1))] : 0f;
    Console.Write($"{i + 1}:{med:F2}/{q90:F2} ");
}
Console.WriteLine();

Console.WriteLine($"\nlatence d'analyse   {analyzer.LatencyMs:F0} ms");
Console.WriteLine($"tempo detecte sur   {tempos.Count * 100 / Math.Max(1, changes.Count)} % des fenetres" +
                  (tempos.Count > 0 ? $" · median {Median(tempos):F1} BPM" : ""));
Console.WriteLine($"changement d'accord p50 {Pct(changes, 50):F2} · p90 {Pct(changes, 90):F2} · p99 {Pct(changes, 99):F2}");
Console.WriteLine($"pentes cumulees     p50 {Pct(slopes, 50):F3} · p95 {Pct(slopes, 95):F3} · max {slopes.Max():F3}");
Console.WriteLine($"tempo               {analyzer.Bpm?.ToString("F1") ?? "—"} BPM");
Console.WriteLine($"frappes             {kicks} kicks · {claps} claps · {hats} charleys");
if (kickAt.Count > 4)
{
    var kg = kickAt.Zip(kickAt.Skip(1), (a, b) => (float)(b - a)).ToList();
    var kmed = Median(kg);
    // LA METRIQUE QUI JUGE LA DETECTION. L'ecart median entre kicks doit tomber sur un
    // multiple simple du temps. S'il colle au minimum autorise, le detecteur sature et
    // compte du bruit ; s'il vaut la moitie d'un temps, il entend les croches.
    var beat = analyzer.Bpm is { } b2 ? 60_000f / b2 : float.NaN;
    Console.WriteLine($"ecart median entre kicks {kmed:F0} ms" +
        (float.IsNaN(beat) ? "" : $"  =  {kmed / beat:F2} temps") +
        $"  ·  minimum autorise {OnsetDetector.MinGapMs:F0} ms");
}
if (kickAt.Count > 8 && gridMs.Count > 0)
{
    // La mediane ne dit rien de la dispersion. Ce qui compte est la part des intervalles
    // qui tombent vraiment sur un multiple du temps : c'est elle qui decide si la grille
    // peut se caler sur ces frappes ou si elle poursuit du bruit.
    var beat = Median(gridMs);
    var kg = kickAt.Zip(kickAt.Skip(1), (a, b) => (float)(b - a)).ToList();
    // ATTENTION AU CRITERE. Exiger un multiple entier du temps rejette les croches, et
    // un kick sur un contretemps est parfaitement musical. On compte donc separement les
    // deux grilles, et la seconde est la seule qui ait un sens musical.
    // Tolerance absolue, en fraction de temps, et non relative a l'unite testee : sans
    // quoi la grille la plus fine est jugee le plus severement, ce qui donne des croches
    // moins souvent justes que des temps — un resultat impossible, puisque tout multiple
    // du temps est aussi un multiple de la croche.
    int Near(float r, float unit) => MathF.Abs(r - unit * MathF.Round(r / unit)) < 0.12f ? 1 : 0;
    // La tolerance doit se resserrer avec la grille, sinon la plus fine est
    // trivialement satisfaite : a plus ou moins 0,12 temps sur un pas de 0,25, on couvre
    // 96 % de l'espace et le chiffre ne prouve plus rien.
    int NearT(float r, float unit, float tol) =>
        MathF.Abs(r - unit * MathF.Round(r / unit)) < tol ? 1 : 0;
    var onBeat = kg.Sum(g => NearT(g / beat, 1f, 0.12f));
    var onEighth = kg.Sum(g => NearT(g / beat, 0.5f, 0.06f));
    var onSixteenth = kg.Sum(g => NearT(g / beat, 0.25f, 0.03f));
    Console.WriteLine($"intervalles sur la grille  temps {onBeat * 100 / kg.Count} %" +
                      $"  ·  croches {onEighth * 100 / kg.Count} %" +
                      $"  ·  doubles {onSixteenth * 100 / kg.Count} %  (sur {kg.Count})");

    var close = syncErr.Count(e => e < 0.1f);
    Console.WriteLine($"frappes bien calees        {close * 100 / syncErr.Count} %" +
                      $"  (ecart de phase sous 0,1 temps)");
}

if (syncErr.Count > 4)
{
    var m = syncErr.Average();
    var sd = MathF.Sqrt(syncErr.Sum(e => (e - m) * (e - m)) / syncErr.Count);
    var beatMs = analyzer.Bpm is { } b3 ? 60_000f / b3 : 625f;
    Console.WriteLine("\ncourbe du tempo, meilleures periodes :");
foreach (var (pb, raw, sc) in analyzer.TempoPeaks(6))
    Console.WriteLine($"   {pb,6:F1} BPM   correlation {raw,6:F3}   score {sc,6:F3}");
Console.WriteLine("   —— hypotheses connues ——");
foreach (var t in new[] { 87f, 90f, 93f, 96f, 104f, 108f, 117f, 45f, 180f })
    Console.WriteLine($"   {t,6:F1} BPM   correlation {analyzer.TempoRawAt(t),6:F3}");
Console.WriteLine($"periode de la grille  mediane {Median(gridMs):F0} ms" +
    (tempoMs.Count > 0 ? $"  ·  tempo publie {Median(tempoMs):F0} ms  ·  ecart {100f * (Median(gridMs) - Median(tempoMs)) / Median(tempoMs):+0.0;-0.0} %" : "  ·  tempo jamais publie"));
Console.WriteLine($"justesse de phase   ecart moyen {m:F3} temps = {m * beatMs:F0} ms" +
                      $"  ·  dispersion {sd:F3}  ·  median {Median(syncErr):F3}");
    Console.WriteLine($"position dans la fenetre  moyenne {offsets.Average():F1} ms sur 21");
}
Console.WriteLine($"registres tonals    {vLow} voix graves · {vMid} medium · {vHigh} aigues");
Console.WriteLine($"indices de structure {chordChanges} changements d'accord · {novelties} ruptures");

// COMBIEN DE TEMPS AVANT DE POUVOIR SE FIER A QUELQUE CHOSE ?
//
// La question s'est posee en regardant un lancement en direct : le tempo publie vagabondait
// entre 84 et 88 pendant trois quarts de minute avant de se poser. Mais « se poser » ne veut
// pas dire la meme chose pour la periode et pour le temps fort, et les confondre menerait a
// regler le mauvais etage.
//
// On mesure donc deux instants distincts, et pour chacun on exige que la valeur TIENNE :
// une grandeur qui frole la bonne valeur une seconde puis repart n'est pas accrochee.
static long? Tient(IReadOnlyList<(long T, float? Bpm, float Conf)> suite,
                   Func<(long T, float? Bpm, float Conf), bool> bon, long duree)
{
    for (var i = 0; i < suite.Count; i++)
    {
        if (!bon(suite[i])) continue;
        var fin = suite[i].T + duree;
        var tenu = true;
        for (var j = i; j < suite.Count && suite[j].T <= fin; j++)
            if (!bon(suite[j])) { tenu = false; break; }
        if (tenu) return suite[i].T;
    }
    return null;
}

if (accroche.Count > 20)
{
    // La reference : le tempo du dernier tiers, quand tout est etabli.
    var tardifs = accroche.Skip(accroche.Count * 2 / 3)
                          .Where(a => a.Bpm is not null).Select(a => a.Bpm!.Value).ToList();
    var reference = tardifs.Count > 0 ? Median(tardifs) : float.NaN;

    const long Tenue = 5000;   // cinq secondes sans repartir
    // LE CRITERE SE MESURE EN BPM, PAS EN POUR CENT, ET CE N'EST PAS UN DETAIL.
    //
    // Le tempo publie bouge par pas d'un BPM entier (TempoTracker.TempoStep) : exiger un
    // pour cent, soit 0,87 BPM a 87, revient a demander une precision plus fine que la
    // quantification. Trois morceaux sur treize paraissaient alors « ne jamais accrocher »
    // alors qu'ils etaient poses a un pas pres.
    //
    // Un BPM et demi laisse passer un pas de publication sans laisser passer une erreur
    // reelle : a 87 BPM, un pas vaut 11 ms sur la noire, deux pas 23 — au-dela on quitte
    // le domaine du reglage pour celui de la faute.
    const float ToleranceBpm = 1.5f;
    var tTempo = float.IsNaN(reference) ? null
        : Tient(accroche, a => a.Bpm is { } b && MathF.Abs(b - reference) < ToleranceBpm, Tenue);
    var tFort = Tient(accroche, a => a.Conf > 0.35f, Tenue);
    var tPublie = Tient(accroche, a => a.Bpm is not null, Tenue);

    string Dire(long? t) => t is { } v ? $"{v / 1000f,5:F1} s" : "  jamais";
    Console.WriteLine($"\naccroche  (tenue cinq secondes)");
    Console.WriteLine($"  un tempo publie      {Dire(tPublie)}");
    Console.WriteLine($"  a 1,5 BPM du tempo final {Dire(tTempo)}   (reference {reference:F1} BPM)");
    Console.WriteLine($"  temps fort au-dessus de 0,35 {Dire(tFort)}");
}

Console.WriteLine($"\nconfiance du temps fort  finale {confidences[^1]:F2} · mediane {Median(confidences):F2}");
Console.WriteLine($"verrouille sur           {confidences.Count(c => c > 0.35f) * 100 / confidences.Count} % des fenetres");
Console.WriteLine($"temps non nomme          {beatHisto[0] * 100 / confidences.Count} % des fenetres");

if (barStarts.Count > 2)
{
    var gaps = barStarts.Zip(barStarts.Skip(1), (a, b) => (float)(b - a)).ToList();
    var med = Median(gaps);
    // Une mesure vaut quatre temps : la comparaison au tempo dit si le compteur suit
    // vraiment la musique ou s'il derive tout seul.
    var expected = analyzer.Bpm is { } bpm ? 4f * 60_000f / bpm : float.NaN;
    var off = gaps.Count(g => MathF.Abs(g - med) > med * 0.25f);
    Console.WriteLine($"\nmesures comptees    {barStarts.Count}");
    Console.WriteLine($"  intervalle median {med:F0} ms (attendu {expected:F0} ms au tempo detecte)");
    Console.WriteLine($"  irreguliers       {off} sur {gaps.Count}");
}

Console.WriteLine($"\nlongueur de phrase  " +
    string.Join("  ", phraseLen.OrderByDescending(k => k.Value)
        .Select(k => $"{k.Key} mesures : {k.Value * 100 / confidences.Count} %")));
var (sBars, sBest, sScores) = analyzer.Section;
Console.WriteLine($"scores de phrase    " + string.Join("  ",
    SectionTracker.Candidates.Select((c, i) => $"{c}:{sScores[i]:F3}")) +
    $"   retenu {sBars}");
var (dOff, dConf, dScores, _) = analyzer.Section is var _ ? analyzer.Downbeat : default;
Console.WriteLine($"ruptures de continuite  {ruptures}" + (ruptures > 0 ? $"  (derniere : {lastReason})" : ""));
Console.WriteLine($"profil du temps fort  offset {dOff} · confiance finale {dConf:F2} · mediane {Median(profileConf):F2}");
Console.WriteLine($"  scores  " + string.Join(" ", dScores.Select((v, i) => $"{i}:{v:+0.000;-0.000}")));
Console.WriteLine($"confiance section   mediane {Median(sectionConf):F2} · max {sectionConf.Max():F2}");
Console.WriteLine($"phrases             {phraseStarts.Count}");
if (phraseStarts.Count > 2)
{
    var pg = phraseStarts.Zip(phraseStarts.Skip(1), (a, b) => (float)(b - a)).ToList();
    var pmed = Median(pg);
    Console.WriteLine($"  intervalle median {pmed / 1000f:F1} s  ·  " +
                      $"reguliers {pg.Count(g => MathF.Abs(g - pmed) < pmed * 0.2f)}/{pg.Count}");
}

var totalBreaks = breakBar.Sum();
// Compteur libre : celui-la n'est jamais realigne, donc la position d'une rupture y a
// un sens. C'est le seul moyen de savoir si les ruptures tombent vraiment toutes les
// 4, 8 ou 16 mesures.
if (freeBreaks.Count >= 4)
{
    Console.WriteLine($"\nsur un compteur de mesures LIBRE, {freeBreaks.Count} ruptures :");
    foreach (var period in new[] { 4, 8, 16 })
    {
        var histo = new int[period];
        foreach (var b in freeBreaks) histo[b % period]++;
        var flat2 = freeBreaks.Count / (float)period;
        var chi2 = histo.Sum(c => (c - flat2) * (c - flat2) / flat2);
        Console.WriteLine($"  modulo {period,2} : {string.Join(" ", histo)}" +
                          $"   ecart au hasard {chi2:F1}");
    }
}

if (totalBreaks >= 4)
{
    Console.WriteLine($"\nou tombent les {totalBreaks} ruptures :");
    Console.WriteLine("  mesure  " + string.Join(" ", breakBar.Select((c, i) => $"{i}:{c}")));
    Console.WriteLine("  temps   " + string.Join(" ", breakBeat.Select((c, i) => $"{i}:{c}")));

    // Une repartition au hasard donnerait autant de ruptures par mesure. L'ecart a cette
    // repartition dit si la phrase existe vraiment, et si elle est de la bonne longueur.
    var flat = totalBreaks / (float)Structure.DefaultPhraseBars;
    var chi = breakBar.Sum(c => (c - flat) * (c - flat) / flat);
    Console.WriteLine($"  ecart au hasard  {chi:F1}  (0 = reparti au hasard, > 14 = concentre)");
    Console.WriteLine("  ATTENTION : biaise. AlignPhrase remet le compteur a zero a chaque");
    Console.WriteLine("  rupture, donc les trouver sur la mesure 0 ne prouve rien.");

    if (breakGaps.Count >= 3)
    {
        var g = breakGaps.OrderBy(x => x).ToList();
        Console.WriteLine($"  espacement median  {g[g.Count / 2]} mesures  " +
                          $"(multiples de 4 : {breakGaps.Count(x => x % 4 == 0)}/{breakGaps.Count})");
    }
}
Console.WriteLine($"tension mediane     {Median(buildups):F2} · maximum {buildups.Max():F2}");

// Ce que le pipeline a mesure sur cette machine, et ce qu'il en a conclu. La decision
// n'est pas ecrite dans le code : elle depend du nombre de coeurs et de ce que coute une
// repartition ici.
var pipe = analyzer.SourcePipeline;
var (seqUs, parUs) = pipe.Cost;
Console.WriteLine($"voies               {(pipe.Parallel ? "PARALLELE" : "SEQUENTIEL")} retenu · " +
                  $"sequentiel {seqUs:F1} us/image · parallele {parUs:F1} us/image");
coutImage.Sort();
var pireImage = coutImage[^1];
var p99 = coutImage[(int)(coutImage.Count * 0.99)];
var enRetard = coutImage.Count(x => x > 21.3);
Console.WriteLine($"cout d'une image    median {coutImage[coutImage.Count / 2]:F2} ms · " +
                  $"99e centile {p99:F2} ms · pire {pireImage:F1} ms · " +
                  $"{enRetard} images au-dessus du pas de 21 ms");

Console.WriteLine($"annonces de tempo   {annonces.Count} : " +
                  string.Join(" · ", annonces.Take(12).Select(a => $"{a.T / 1000f:F0}s {a.Bpm:F1}")));
// Les deux fonctions de detection se ressemblent-elles ? Une correlation proche de 1
// signifierait qu'elles prennent les memes decisions, et qu'en changer ne sert a rien.
if (fluxE.Count > 10)
{
    double mE = fluxE.Average(), mC = fluxC.Average();
    double num = 0, dE = 0, dC = 0;
    for (var i = 0; i < fluxE.Count; i++)
    {
        double a = fluxE[i] - mE, b = fluxC[i] - mC;
        num += a * b; dE += a * a; dC += b * b;
    }
    var r = dE > 0 && dC > 0 ? num / Math.Sqrt(dE * dC) : 0;
    Console.WriteLine($"flux energie/complexe  correlation {r:F3} · " +
                      $"moyennes {mE:F1} et {mC:F1}");
}
// LES FAMILLES DE FRAPPES : combien d'instruments distincts, et sont-ils stables ?
//
// Une famille qui ne frappe qu'une ou deux fois est un accident — un craquement, une
// frappe isolee. Une famille qui revient des dizaines de fois est un instrument du morceau.
{
    var fam = analyzer.Evenements.Familles;
    Console.WriteLine();
    var acc = analyzer.Accord;
// OU CHAQUE CHOSE TOMBE DANS LA MESURE.
//
// L'IDEE. Le tempo est ce que ce projet mesure le mieux. Si une frappe revient toujours au
// meme endroit du cycle de quatre temps, alors la grille suffit a l'annoncer : on n'attend
// plus de la detecter, on sait quand elle vient. Et savoir quand elle vient, c'est pouvoir
// l'allumer AVANT que le son n'arrive — ce qui retire du retard au lieu d'en ajouter.
//
// CE QUE MESURE CE TABLEAU. Pour chaque voie, la repartition de ses frappes sur seize
// cases. Le chiffre qui compte est la part tombant dans les quatre cases les plus
// frequentees : le hasard en donnerait 25 %, un motif regulier bien davantage. En dessous
// de 40 % il n'y a pas de motif a annoncer, et il faudra continuer a detecter.
void Motif(string nom, int[] cases, int total)
{
    if (total < 8) { Console.WriteLine($"  {nom,-10} trop peu de frappes ({total})"); return; }
    var rangs = Enumerable.Range(0, 16).OrderByDescending(c => cases[c]).ToArray();
    var top4 = rangs.Take(4).Sum(c => cases[c]) * 100f / total;
    var top1 = cases[rangs[0]] * 100f / total;
    var dessin = string.Concat(Enumerable.Range(0, 16).Select(c =>
    {
        var part = cases[c] / (float)Math.Max(1, cases[rangs[0]]);
        return part > 0.75f ? '#' : part > 0.45f ? '+' : part > 0.15f ? '.' : ' ';
    }));
    // Les temps forts sont les cases 0, 4, 8 et 12.
    var surTemps = (cases[0] + cases[4] + cases[8] + cases[12]) * 100f / total;
    Console.WriteLine($"  {nom,-10} |{dessin}|  4 cases {top4,3:F0} %  ·  la mieux lotie {top1,3:F0} %" +
                      $"  ·  sur les temps {surTemps,3:F0} %  ({total} frappes)");
}

// « instants=<prefixe> » : ecrit nos frappes et nos temps, pour les confronter a une
// implementation de reference. Toutes nos autres mesures se notent contre notre propre
// grille ; celle-ci est la seule qui puisse nous contredire.
if (bpmTrace is not null && traceBpm is not null)
{
    File.WriteAllLines(bpmTrace, traceBpm);
    Console.WriteLine($"\n{traceBpm.Count} tempos ecrits vers {bpmTrace}");
}

// « profils=<fichier.json> » : les gabarits appris, dans l'ordre du grave a l'aigu, avec
// ce qu'il faut pour les relire : l'axe logarithmique et le glissement.
//
// C'EST CE QUI PERMET D'ENTENDRE CE QU'UNE SOURCE ENTEND. Toutes nos mesures disent si une
// source est REGULIERE ; aucune ne dit si elle contient ce qu'elle pretend contenir. Avec
// les profils, `outils/extraire.py` reconstruit chaque source en son, et l'oreille tranche
// en dix secondes ce qu'aucun chiffre n'a su dire.
//
// On exporte a la FIN de la passe : les profils s'apprennent, et ceux du debut ne decrivent
// que du bruit.
if (args.FirstOrDefault(a => a.StartsWith("profils="))?[8..] is { } fichierProfils)
{
    var separation = analyzer.Separation;
    var bins = separation.Bins;
    var profil = new float[bins];
    var lignes = new List<string>
    {
        "{",
        $"  \"fichier\": \"{Path.GetFileName(path).Replace("\\", "/")}\",",
        $"  \"taux\": {rate},",
        $"  \"fenetre\": {SourceSeparator.FenetreLog},",
        $"  \"cases\": {ProfileLearner.NLog},",
        $"  \"parOctave\": {ProfileLearner.ParOctave},",
        $"  \"f0\": {separation.F0.ToString("G6", System.Globalization.CultureInfo.InvariantCulture)},",
        $"  \"accordageCents\": {separation.AccordageCents.ToString("G6", System.Globalization.CultureInfo.InvariantCulture)},",
        $"  \"positions\": {ProfileLearner.Positions},",
        $"  \"longueur\": {bins},",
        $"  \"pret\": {(separation.Pret ? "true" : "false")},",
        $"  \"actives\": {separation.Actives},",
        "  \"gabarits\": [",
    };
    for (var r = 0; r < separation.Actives; r++)
    {
        separation.ProfilOrdonne(r, profil);
        var vals = string.Join(",", profil.Select(v =>
            v.ToString("G6", System.Globalization.CultureInfo.InvariantCulture)));
        lignes.Add($"    [{vals}]" + (r < separation.Actives - 1 ? "," : ""));
    }
    lignes.Add("  ]");
    lignes.Add("}");
    File.WriteAllLines(fichierProfils, lignes);
    Console.WriteLine($"\n{separation.Actives} gabarits ecrits vers {fichierProfils}"
                      + (separation.Pret ? "" : "  — ATTENTION : rien n'a ete appris"));
}

if (fichierChroma is not null && chromaMoyen is not null && chromaImages > 0)
    File.WriteAllText(fichierChroma, string.Join(",", chromaMoyen.Select(v =>
        (v / chromaImages).ToString("G6", System.Globalization.CultureInfo.InvariantCulture))));

if (args.FirstOrDefault(a => a.StartsWith("instants="))?[9..] is { } prefixe)
{
    File.WriteAllLines(prefixe + "-kicks.txt", kickAt.Select(t => (t / 1000.0).ToString("F3",
        System.Globalization.CultureInfo.InvariantCulture)));
    File.WriteAllLines(prefixe + "-temps.txt", nosTemps.Select(t => (t / 1000.0).ToString("F3",
        System.Globalization.CultureInfo.InvariantCulture)));
    File.WriteAllLines(prefixe + "-repli.txt", repliAt.Select(t => (t / 1000.0).ToString("F3",
        System.Globalization.CultureInfo.InvariantCulture)));
    File.WriteAllLines(prefixe + "-claps.txt", clapAt.Select(t => (t / 1000.0).ToString("F3",
        System.Globalization.CultureInfo.InvariantCulture)));
    File.WriteAllLines(prefixe + "-charleys.txt", hatAt.Select(t => (t / 1000.0).ToString("F3",
        System.Globalization.CultureInfo.InvariantCulture)));
    Console.WriteLine($"\n{kickAt.Count} frappes, {clapAt.Count} claps, {hatAt.Count} charleys " +
                      $"et {nosTemps.Count} temps ecrits vers {prefixe}-*.txt");
}

Console.WriteLine("\nla phase elle-meme, sur toutes les images  " +
    string.Join(" ", phaseHisto.Select(c => (c * 100 / Math.Max(1, phaseHisto.Sum())).ToString())) + " %");
Console.WriteLine("\nposition dans la mesure  (16 cases, une par double croche ; hasard = 25 % sur 4 cases)");
Motif("kick", kickCase, kickTotalC);
Motif("clap", clapCase, clapTotalC);
Motif("charley", hatCase, hatTotalC);
for (var fm = 0; fm < EventFamilies.Max; fm++)
    if (famTotal[fm] >= 8) Motif($"famille {fm}", famCase[fm], famTotal[fm]);
Console.WriteLine();

    Console.WriteLine($"familles de frappes  {fam.Connues} distinctes · " +
                      $"accord avec la grille {acc.Accord:F2} " +
                      $"({acc.Votantes} familles votantes)");
    for (var i = 0; i < fam.Connues; i++)
    {
        var c = fam.CentreDe(i);
        var v = fam.VuesDe(i);
        // LA REGULARITE D'UNE FAMILLE : est-elle le pouls du morceau ?
        //
        // Une famille dont les intervalles se ressemblent frappe en mesure ; une famille
        // dont ils partent dans tous les sens est un ornement, ou du bruit. C'est un
        // indice que les bandes de frequence ne donnent pas : la pulsation n'est pas
        // forcement dans les graves.
        var t = famAt[i];
        var regul = "—";
        var surTemps = "";
        if (t.Count > 4)
        {
            var iv = new List<double>();
            for (var k = 1; k < t.Count; k++) iv.Add(t[k] - t[k - 1]);
            iv.Sort();
            var med = iv[iv.Count / 2];
            // Ecart median a la mediane : robuste, contrairement a un ecart-type que
            // deux intervalles aberrants suffisent a doubler.
            var ecarts = iv.Select(x => Math.Abs(x - med)).OrderBy(x => x).ToList();
            var mad = ecarts[ecarts.Count / 2];
            regul = med > 0 ? $"{1.0 - Math.Min(1.0, mad / med):F2}" : "—";

            if (tempos.Count > 0)
            {
                var temps = 60000.0 / Median(tempos);
                var justes = iv.Count(x => Math.Abs(x / temps - Math.Round(x / temps)) < 0.15);

                // A QUEL TEMPO CETTE FAMILLE BAT-ELLE, SI ELLE BAT SEULE ?
                //
                // Une famille reguliere qui ne tombe pas sur la grille dit quelque chose :
                // ou bien elle joue une subdivision, ou bien c'est la grille qui se trompe.
                // On publie donc le tempo qu'elle impliquerait, et le rapport a celui qu'on
                // a detecte — un rapport proche de 1, 2 ou 0,5 est une subdivision ; un
                // rapport batard est un desaccord.
                var bpmFamille = 60000.0 / med;
                var rapport = bpmFamille / Median(tempos);
                surTemps = $" · {100.0 * justes / iv.Count,3:F0} % sur la grille" +
                           $" · bat a {bpmFamille,5:F1} BPM (x{rapport:F2})";
            }
        }

        var barre = new string('#', Math.Min(24, v / 6));
        Console.WriteLine($"  famille {i} : {v,4} frappes {barre,-24} " +
                          $"brillance {c.Brillance:F2} · piquant {c.Piquant:F2} · " +
                          $"regularite {regul}{surTemps}");
    }
}

Console.WriteLine("maturite des sources  (confiance 0,6 atteinte a)");
for (var r = 0; r < Voices.Registers; r++)
{
    // Ce qui est publie, donc ce que le renderer voit : la separation par timbre.
    var e = analyzer.Derniere.Voices.LaneAt(r);
    Console.WriteLine($"  source {r} : nommable a " +
                      (murAt[r] < 0 ? " jamais" : $"{murAt[r] / 1000f,6:F1} s") + " · " +
                      "assez ecoutee a " +
                      (assezAt[r] < 0 ? " jamais" : $"{assezAt[r] / 1000f,6:F1} s") + " · " +
                      $"nettete {e.Sharpness:F2} · brillance {e.Brightness:F2}");
}

// ------------------------------------------------------------------ OU PASSE LE TEMPS
//
// Deux retards, et il ne faut pas les confondre. Le temps de CALCUL se reduit en ecrivant
// mieux ; le retard ALGORITHMIQUE ne se reduit pas du tout — il faut avoir entendu une
// fenetre avant de la transformer, et la suivante avant de dire qu'on etait sur un sommet.
{
    var e = analyzer.Etapes;
    var total = e.Totale();
    var large = 0.0;
    for (var i = 0; i < Etapes.Noms.Length; i++) large = Math.Max(large, e.Moyenne(i));

    Console.WriteLine();
    Console.WriteLine($"CALCUL, etage par etage  ({e.Images} images · {total:F0} us au total, "
                      + $"soit {total / 1000 / (hop * 1000f / rate) * 100:F1} % du pas de "
                      + $"{hop * 1000f / rate:F1} ms)");
    Console.WriteLine();

    for (var i = 0; i < Etapes.Noms.Length; i++)
    {
        var us = e.Moyenne(i);
        var n = large > 0 ? (int)Math.Round(us / large * 46) : 0;
        Console.WriteLine($"  {Etapes.Noms[i],-16} {new string('#', n)}{new string('.', 46 - n)} "
                          + $"{us,7:F1} us   pire {e.Pire(i),7:F1}");
    }

    // Le pas d'une fenetre, et ce que le calcul en occupe.
    var pasMs = hop * 1000f / rate;
    var occupe = (int)Math.Round(total / 1000 / pasMs * 46);
    Console.WriteLine();
    Console.WriteLine($"  {"pas de fenetre",-16} {new string('=', 46)} {pasMs * 1000,7:F0} us");
    Console.WriteLine($"  {"occupe par nous",-16} {new string('#', Math.Min(46, occupe))}"
                      + $"{new string('.', Math.Max(0, 46 - occupe))} {total,7:F0} us");

    Console.WriteLine();
    Console.WriteLine("RETARD ALGORITHMIQUE  (ce qu'aucune optimisation ne retire)");
    Console.WriteLine();

    var etages = new (string Nom, float Ms, string Pourquoi)[]
    {
        ("fenetre", pasMs, "il faut l'avoir entendue en entier avant de la transformer"),
        ("separation H/P", analyzer.Separating ? pasMs * 3 : 0f,
            analyzer.Separating ? "voir si un bin dure demande de voir la suite" : "coupee"),
        ("sommet d'attaque", pasMs, "un maximum ne se reconnait qu'apres la valeur suivante"),
        ("calcul", (float)(total / 1000), "mesure ci-dessus"),
    };

    var cumul = 0f;
    foreach (var (nom, ms, pourquoi) in etages)
    {
        cumul += ms;
        var n = (int)Math.Round(ms / 70f * 46);
        Console.WriteLine($"  {nom,-17} {new string('#', n)}{new string('.', Math.Max(0, 46 - n))} "
                          + $"{ms,6:F1} ms   {pourquoi}");
    }

    Console.WriteLine($"  {"─── total",-17} {new string(' ', 46)} {cumul,6:F1} ms   "
                      + (cumul <= 40 ? "sous le seuil ou l'oeil decroche (40 ms)"
                                     : "AU-DESSUS du seuil de 40 ms"));
    // ------------------------------------------------------ LA CHAINE ENTIERE
    //
    // Ce que le projet maitrise, ce qu'il subit, et ce qui reste a l'unite de rendu. Le
    // budget se compte a partir du seuil ou l'oeil cesse de lier l'image au son.
    // LES SEUILS VIENNENT DE LA LITTERATURE, PAS D'UNE INTUITION.
    //
    // Notre cas est celui d'une image qui suit un son : le son sort de la table, le mur
    // reagit. C'est l'asynchronie « video en retard », et elle est mieux toleree que
    // l'inverse — l'oeil pardonne une image tardive plus qu'un son tardif.
    //
    //   EBU R37 (2007)        video en retard > 40 ms  : hors norme de diffusion
    //   ITU-R BT.1359-1       video en retard > 45 ms  : detectable
    //                         video en retard > 90 ms  : inacceptable
    //   Laboratoire           des 20 ms sur stimulus controle et transitoire net
    //
    // Un kick est un transitoire net, donc on vise le bas de la fourchette.
    const float SeuilNorme = 40f;      // EBU R37
    const float SeuilDetect = 45f;     // ITU-R BT.1359-1, detectabilite
    const float SeuilLimite = 90f;     // ITU-R BT.1359-1, acceptabilite

    var chaine = new (string Nom, float Ms, string Qui)[]
    {
        // MESUREE, ET NON PRISE POUR ARGENT COMPTANT.
        //
        // Le budget portait ici 20 ms, la valeur demandee a parec. Le serveur audio en rend
        // beaucoup moins : 4,4 ms de tampon reel pour 20 demandes, 3,3 pour 5. Le budget
        // etait donc surestime de 15 ms, et c'est un poste qu'on croyait couteux alors qu'il
        // ne l'est pas.
        //
        // Descendre reste utile, mais pour la regularite plus que pour le retard : mesuree
        // sur douze secondes, machine chargee sur ses huit coeurs et le serveur en marche,
        // la gigue d'arrivee des blocs tombe de 3,1 ms a 0,9 — et aucun bloc n'arrive en
        // retard, meme a 2 ms de demande.
        ("capture PulseAudio", 3.3f,               "mesure · 5 ms demandes, tampon reel"),
        ("fenetre d'analyse",   pasMs,             "incompressible · 1024 echantillons"),
        ("sommet d'attaque",    pasMs,             "incompressible · un pic se voit apres"),
        ("calcul",              (float)(total / 1000), "maitrise · 8,8 % du pas"),
        ("anneau partage",      0.0015f,           "maitrise · 1,5 us, sans verrou"),
    };

    Console.WriteLine();
    Console.WriteLine("LA CHAINE ENTIERE, DU MICRO AU PAQUET");
    Console.WriteLine();

    var avantGpu = 0f;
    foreach (var (nom, ms, qui) in chaine)
    {
        avantGpu += ms;
        var n = (int)Math.Round(ms / 25f * 30);
        Console.WriteLine($"  {nom,-19} {new string('#', Math.Min(30, n))}"
                          + $"{new string('.', Math.Max(0, 30 - Math.Min(30, n)))} "
                          + $"{ms,6:F1} ms   {qui}");
    }

    Console.WriteLine($"  {"─── avant le GPU",-19} {new string(' ', 30)} {avantGpu,6:F1} ms");
    Console.WriteLine();
    Console.WriteLine("  CE QU'IL RESTE POUR LE RENDU ET L'ECRAN");
    Console.WriteLine();

    foreach (var (nom, seuil) in new[]
             {
                 ("EBU R37 · norme de diffusion", SeuilNorme),
                 ("ITU-R BT.1359 · detectable", SeuilDetect),
                 ("ITU-R BT.1359 · inacceptable", SeuilLimite),
             })
    {
        var reste = seuil - avantGpu;
        Console.WriteLine($"  {nom,-30} seuil {seuil,3:F0} ms → reste {reste,6:F1} ms"
                          + (reste < 0 ? "   DEPASSE" : reste < 20 ? "   tres serre" : ""));
    }

    Console.WriteLine();
    Console.WriteLine("  Un videoprojecteur consomme a lui seul 16 a 33 ms selon son mode de");
    Console.WriteLine("  traitement d'image. Un modele de mapping en mode faible latence");
    Console.WriteLine("  descend vers 16 ms, ce qui laisse alors de quoi travailler.");
    Console.WriteLine();
    Console.WriteLine("  CE QUI SAUVE LA MISE : on ne reagit pas au kick, on l'attend.");
    Console.WriteLine("  L'horloge a verrouillage de phase le declenche a l'instant prevu et");
    Console.WriteLine("  peut meme partir en avance. Sur cet evenement-la, le budget GPU n'est");
    Console.WriteLine("  plus borne par le seuil mais par la stabilite du tempo. Tout ce qui");
    Console.WriteLine("  n'est pas periodique — clap irregulier, voix, rupture — subit les");
    Console.WriteLine($"  {avantGpu:F0} ms ci-dessus et n'a plus de marge.");
}

var sep = analyzer.Separation;
Console.WriteLine($"apprentissage NMF   {sep.Apprentissages} fois · moyenne {sep.ApprentissageMs:F1} ms · " +
                  $"pire {sep.ApprentissagePireMs:F1} ms  (une image dure 21 ms)");
// COMBIEN DE SOURCES, ET POURQUOI. Le nombre n'est plus impose : on montre ce que le
// balayage a mesure a chaque pas, pour que « 4 » se lise comme un coude et non un caprice.
Console.WriteLine($"accordage           {sep.AccordageCents:+0;-0} cents" + (MathF.Abs(sep.AccordageCents) < 1f ? "  (axe standard)" : "  (axe decale d'autant)"));
if (analyzer.Camelot is { } camelot)
    Console.WriteLine($"gamme               {camelot}   accord du chromagramme {analyzer.AccordGamme:F2}" + (analyzer.AccordGamme < 0.6f ? "   FICHE CONTREDITE" : ""));
Console.WriteLine($"sources retenues    {sep.Actives}" + (sep.ChoixFait ? "" : "  (provisoire, le balayage n'a pas encore eu lieu)")
                  + (sep.Verrou ? "   VERROUILLE : le morceau est su, plus de reapprentissage" : ""));
// LE MOTIF DE CHAQUE CASE : seize cases de la mesure, # la ou la source monte franchement.
{
    Span<float> motif = stackalloc float[MotifSources.Cases];
    for (var r = 0; r < sep.Publiees; r++)
    {
        analyzer.Motifs.Motif(r, motif);
        var dessin = new System.Text.StringBuilder();
        foreach (var v in motif) dessin.Append(v >= 0.66f ? '#' : v >= 0.33f ? '+' : '.');
        var caractere = analyzer.Derniere.Voices.Caracteres is { } cs && r < cs.Length ? cs[r] : 0.5f;
        var degre = analyzer.Derniere.Voices.Degres is { } ds && r < ds.Length ? ds[r] : Gamme.Inconnu;
        Console.WriteLine($"   case {r + 1}{(r == sep.RangReste ? " (reste)" : "")}  motif [{dessin}]  stabilite {analyzer.Motifs.Stabilite(r):F2}  sur {analyzer.Motifs.MesuresVues(r)} mesures   caractere {caractere:F2} ({(caractere < 0.45f ? "frappe" : caractere > 0.8f ? "tient" : "pince")})   dominance {sep.DominanceOrdonnee(r):F2}" + (degre != Gamme.Inconnu ? $"   degre {Gamme.Nom(degre)}" : ""));
    }
}
foreach (var b in sep.Bilans)
    Console.WriteLine($"   {b.K} sources  inexplique {100 * b.Reste,5:F1} %   pire doublon {b.Doublon:F2}");
Console.WriteLine($"ruptures            {drops.Count}" +
                  (drops.Count > 0 ? "  a " + string.Join(", ", drops.Select(d => $"{d / 1000f:F0} s")) : ""));
if (exportTo is not null)
{
    File.WriteAllText(exportTo,
        "{\"rate\":" + rate + ",\"window\":" + hop +
        ",\"latenceMs\":" + analyzer.LatencyMs.ToString("F0") +
        ",\"champs\":[\"t\",\"rms\",\"bandes\",\"drapeaux\",\"voixGrave\"," +
        "\"voixMedium\",\"voixAigue\",\"ouverture\",\"brillance\",\"densite\"," +
        "\"bpm\",\"temps\",\"tension\",\"nouveaute\",\"confianceTemps\",\"tonalite\",\"pitchGrave\",\"pitchMedium\",\"pitchAigu\",\"registres\",\"contours\",\"attaques\",\"ecoutes\",\"nettetes\",\"noms\",\"bpmAnnonce\",\"annonce\",\"derive\"]" +
        ",\"images\":[\n" + string.Join(",\n", exported) + "\n]}");
    Console.WriteLine($"\n{exported.Count} images exportees vers {exportTo}");
}

return 0;

static float Pct(List<float> v, int p)
{
    if (v.Count == 0) return 0f;
    var c = new List<float>(v);
    c.Sort();
    return c[Math.Clamp(c.Count * p / 100, 0, c.Count - 1)];
}

static float Median(List<float> v)
{
    var c = new List<float>(v);
    c.Sort();
    return c.Count == 0 ? 0f : c[c.Count / 2];
}
