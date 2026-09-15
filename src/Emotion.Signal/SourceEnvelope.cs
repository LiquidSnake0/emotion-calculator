namespace Emotion.Signal;

/// <summary>
/// Comment une source ATTAQUE et comment elle TIENT.
///
/// CE QUE LE SYSTEME NE SAVAIT PAS DIRE.
///
/// Chaque source publiait son niveau, sa hauteur et un drapeau de frappe. Rien de tout
/// cela ne distingue une corde pincee d'un souffle : l'une monte d'un coup puis meurt,
/// l'autre s'installe et ne frappe jamais. Les deux produisent le meme niveau moyen, la
/// meme hauteur, et le rendu leur donnait donc le meme mouvement.
///
/// C'est le DJ qui l'a formule : « un instrument a corde c'est une frappe suivie d'une
/// onde courte ou longue, un instrument a vent c'est en continu, il ne frappe pas ». Deux
/// grandeurs suffisent a porter cette difference.
///
///   PIQUE   a quel point la source monte d'un coup, 0 a 1.
///           Une corde pincee, une frappe : proche de 1. Un archet, un souffle : proche de 0.
///
///   TENUE   a quel point elle reste au niveau qu'elle a atteint, 0 a 1.
///           Un souffle : proche de 1. Un pizzicato : proche de 0.
///
/// LES DEUX SONT INDEPENDANTES, et c'est ce qui les rend utiles. Une note d'orgue a un
/// pique faible et une tenue forte ; un woodblock, l'inverse ; un piano, un pique fort et
/// une tenue moyenne — c'est-a-dire exactement la « frappe suivie d'une onde courte ou
/// longue » du DJ, ou la longueur de l'onde EST la tenue.
///
/// CE SONT DES DESCRIPTEURS, PAS DES EVENEMENTS, et cette distinction commande tout ce qui
/// se passe en aval. Un evenement est instantane et ne doit jamais etre lisse — une
/// impulsion lissee n'est plus une impulsion. Ces deux-ci decrivent au contraire la NATURE
/// d'une source, qui ne change pas d'une fenetre a l'autre : elles se moyennent sur
/// plusieurs secondes, se transportent comme le niveau, et s'interpolent comme lui. Le
/// drapeau de frappe, lui, reste brut.
/// </summary>
public sealed class SourceEnvelope
{
    /// <summary>
    /// Sur combien de temps la crete et la moyenne sont observees, en secondes.
    ///
    /// Une seconde et demie couvre deux temps du repertoire : assez pour qu'une note
    /// entiere y tienne — attaque, chute et silence — et assez court pour qu'un changement
    /// d'instrument se voie avant la fin de la phrase.
    /// </summary>
    public const float FenetreS = 1.5f;

    /// <summary>
    /// Temps de montee au-dela duquel on ne parle plus d'attaque, en secondes.
    ///
    /// Quarante millisecondes, soit deux fenetres d'analyse. C'est aussi le seuil ou l'oeil
    /// cesse de lier une image a un son — au-dela, ce qui monte n'est plus percu comme une
    /// frappe mais comme une arrivee, et le rendu doit le montrer autrement.
    /// </summary>
    public const float MonteeMaxS = 0.040f;

    private readonly float _frameS;
    private readonly float[] _crete;
    private readonly float[] _moyenne;
    private readonly float[] _precedent;
    private readonly float[] _pique;
    private readonly float[] _muet;      // depuis combien de secondes la source se tait
    private readonly bool[] _frappe;     // la source vient-elle de frapper, sur cette image
    private readonly int[] _depuisFrappe;
    private readonly float[] _caractere; // part tenue, moyennee sur la duree : 0 frappe, 1 tient
    private readonly bool[] _caractereConnu;
    private readonly float _oubliCaractere;

    // L'ETAT AU MOMENT OU LE SILENCE COMMENCE.
    //
    // On ne sait pas encore s'il s'agit d'un blanc entre deux notes ou d'un retrait : la
    // duree seule les separe, et elle n'est connue qu'apres coup. On continue donc de
    // mesurer — c'est indispensable au pizzicato — mais on garde de quoi revenir en
    // arriere. Si le silence dure, on restaure : la source retrouve exactement ce qu'elle
    // etait a sa derniere note, sans la seconde et demie de decroissance qu'elle vient de
    // subir pour rien.
    private readonly float[] _creteGardee;
    private readonly float[] _moyenneGardee;
    private readonly float[] _piqueGarde;
    private readonly bool[] _restaure;
    private readonly float _oubli;

    public SourceEnvelope(int sources, float frameSeconds)
    {
        _frameS = MathF.Max(1e-4f, frameSeconds);
        _crete = new float[sources];
        _moyenne = new float[sources];
        _precedent = new float[sources];
        _pique = new float[sources];
        _muet = new float[sources];
        _frappe = new bool[sources];
        _depuisFrappe = new int[sources];
        _caractere = new float[sources];
        _caractereConnu = new bool[sources];
        _oubliCaractere = MathF.Exp(-_frameS / CaractereS);
        _creteGardee = new float[sources];
        _moyenneGardee = new float[sources];
        _piqueGarde = new float[sources];
        _restaure = new bool[sources];

        // L'oubli est exprime en fenetres pour ne pas dependre du taux d'echantillonnage :
        // une constante en fenetres ferait glisser la mesure de huit pour cent entre 44,1
        // et 48 kHz, exactement comme le contour melodique s'y est deja fait prendre.
        _oubli = MathF.Exp(-_frameS / FenetreS);
    }

    /// <summary>
    /// En dessous de cette fraction de SA PROPRE CRETE, la source ne joue pas.
    ///
    /// UNE FRACTION D'ELLE-MEME, ET NON UN SEUIL ABSOLU. Le niveau d'une source est
    /// normalise par le maximum des six : il dit « par rapport a la plus forte », pas
    /// « en soi ». Un seuil absolu de deux pour cent revenait donc a demander a chaque
    /// source d'atteindre deux pour cent de la source dominante — ce qu'une source
    /// discrete ne fait jamais.
    ///
    /// Mesure sur un morceau du bac : le niveau median des six sources valait <b>0,000</b>,
    /// elles passaient de 53 a 82 pour cent du temps sous ce seuil, et se declaraient
    /// absentes une moitie du temps. Une source qui s'eteignait avait « de la peine a se
    /// rallumer », selon le mot du DJ — et pour cause : pour revenir, il lui fallait
    /// rivaliser avec celle qui dominait.
    ///
    /// Rapportee a sa propre crete, la question redevient la bonne : joue-t-elle, elle,
    /// par rapport a ce qu'elle joue d'habitude.
    /// </summary>
    public const float Silence = 0.02f;

    /// <summary>
    /// Plancher absolu, pour ne pas prendre du bruit numerique pour une source.
    ///
    /// Sans lui, une source dont la crete a decru jusqu'a rien verrait son seuil decroitre
    /// avec elle et se croirait presente sur son propre souffle.
    /// </summary>
    public const float SilencePlancher = 0.002f;

    /// <summary>
    /// Combien de temps de silence avant de considerer que la source a quitte l'arrangement.
    ///
    /// IL FAUT DEUX SILENCES DIFFERENTS, ET LES CONFONDRE CASSE LA MESURE.
    ///
    /// Le silence ENTRE DEUX NOTES est ce qui fait la tenue d'un pizzicato : c'est lui qui
    /// abaisse la moyenne sous la crete. Le geler reviendrait a mesurer un pizzicato comme
    /// un souffle, c'est-a-dire a detruire le descripteur qu'on vient de construire.
    ///
    /// Le silence d'un RETRAIT est autre chose : la source ne joue plus du tout pendant des
    /// mesures. C'est celui-la qu'il faut geler, sans quoi le violon oublie qu'il etait un
    /// violon pendant le creux et le rendu lui donne le geste d'un souffle a son retour.
    ///
    /// La duree les separe. Une seconde et demie porte deux temps du repertoire : aucune
    /// note n'y laisse un blanc aussi long, et un creux d'arrangement les depasse toujours.
    /// SEULEMENT, UNE SECONDE ET DEMIE N'EST PAS UNE DUREE MUSICALE. Mesure sur un morceau
    /// du bac : les six sources se declaraient absentes de quatorze a soixante pour cent du
    /// temps. Les activations de la separation sont tres piquees — mediane a zero, neuvieme
    /// decile entre 0,26 et 0,56 — donc une source joue par bouffees et se tait entre.
    ///
    /// « Etre en retrait » n'est pas une propriete physique mais musicale : c'est n'avoir
    /// rien joue PENDANT DEUX MESURES. Le seuil suit donc le tempo, et vaut 5,5 s a 87 BPM
    /// contre 2,8 s a 170. Sans tempo connu, on retombe sur la fenetre d'observation.
    /// </summary>
    public float RetraitS =>
        _tempsMs > 0f ? _tempsMs * MesuresDeRetrait * TempsParMesure / 1000f : FenetreS;

    /// <summary>Mesures de silence avant de parler de retrait.</summary>
    public const int MesuresDeRetrait = 2;

    /// <summary>Temps par mesure. Le repertoire est en quatre.</summary>
    public const int TempsParMesure = 4;

    private float _tempsMs;

    /// <summary>Duree d'un temps, en millisecondes. Zero tant qu'on ne la connait pas.</summary>
    public void Tempo(float? bpm)
    {
        if (bpm is { } b && b > 20f && b < 400f) _tempsMs = 60_000f / b;
    }

    /// <summary>Une fenetre d'analyse, pour une source.</summary>
    /// <summary>
    /// LA FRAPPE DE LA SOURCE, TIREE DE SON PROPRE NIVEAU.
    ///
    /// Le bit de frappe publie par source venait des registres de frequence, rang par rang :
    /// mesure sur Passepartout, les trois sources rendaient trois morses quasi identiques
    /// (815, 893, 878 frappes) au niveau du hasard contre les attaques reelles de chaque
    /// stem. Le GPU aurait recu « la source 3 frappe » quand la bande 3 frappait.
    ///
    /// Ici la frappe est une montee franche du niveau de la source elle-meme : au moins
    /// <see cref="SeuilFrappe"/> de sa crete en une image, au-dessus de
    /// <see cref="PlancherFrappe"/>, et pas deux en moins de <see cref="ReposFrappe"/>
    /// images. Mesure sur la basse de Passepartout, contre les attaques du stem : 83 % des
    /// frappes tombent sur une attaque reelle, contre 44 % au hasard. Le piano, lui, n'a pas
    /// de juge automatique fiable — meme le stem extrait a l'oreille ne s'accorde qu'a 34 %
    /// avec les attaques de `other` — c'est la touche espace qui le juge.
    /// </summary>
    public const float SeuilFrappe = 0.15f;
    public const float PlancherFrappe = 0.35f;
    public const int ReposFrappe = 4;

    public bool Frappe(int rang) => (uint)rang < (uint)_frappe.Length && _frappe[rang];

    /// <summary>
    /// LE CARACTERE D'UNE SOURCE : frappe-t-elle, ou tient-elle ? Sur la duree, pas sur
    /// l'image.
    ///
    /// C'est l'octet que le rendu attend pour choisir son geste — « la couleur change au
    /// rythme de ce qui est frappe, les coins s'etirent au rythme d'un souffle » — et rien
    /// d'instantane ne le dit : pique et tenue decrivent l'image, pas l'instrument. Ici,
    /// la tenue (moyenne rapportee a la crete, sur 1,5 s) est moyennee sur vingt secondes.
    ///
    /// Mesure sur trois titres, en regime final : le reste (la batterie) vaut 0,22 a 0,38 ;
    /// les nappes et la basse 0,84 a 0,92 ; ce qui est pince (le piano de Passepartout, la
    /// troisieme source de Glyph Chamber) 0,65 a 0,75. Zero = frappe, un = tient.
    /// </summary>
    public const float CaractereS = 20f;

    public float Caractere(int rang) =>
        (uint)rang < (uint)_caractere.Length ? Math.Clamp(_caractere[rang], 0f, 1f) : 0.5f;

    public void Feed(int rang, float niveau)
    {
        if ((uint)rang >= (uint)_crete.Length) return;
        niveau = Math.Clamp(niveau, 0f, 1f);
        _frappe[rang] = false;
        _depuisFrappe[rang]++;

        // UNE ABSENCE N'EST PAS UN CHANGEMENT, ET LES CONFONDRE FAIT OUBLIER CE QU'ON SAIT.
        //
        // Un violon qui se tait reste un violon. La mesure, elle, decroissait pendant son
        // silence : au bout d'une seconde et demie de creux — ce qui arrive chaque fois que
        // le morceau se vide et laisse une melodie seule — la source avait oublie qu'elle
        // etait pincee, et le rendu lui rendait le geste d'un souffle a son retour.
        //
        // Un retrait gele donc la mesure. Voir <see cref="RetraitS"/> pour pourquoi il faut
        // une duree et non un simple seuil : le silence entre deux notes, lui, doit
        // continuer de compter.
        var seuil = MathF.Max(SilencePlancher, Silence * _crete[rang]);
        if (niveau >= seuil)
        {
            // Elle joue : on repart de zero, et l'on garde cet etat au cas ou le prochain
            // silence serait un retrait.
            _muet[rang] = 0f;
            _restaure[rang] = false;
            _creteGardee[rang] = _crete[rang];
            _moyenneGardee[rang] = _moyenne[rang];
            _piqueGarde[rang] = _pique[rang];
        }
        else
        {
            _muet[rang] += _frameS;
            if (_muet[rang] >= RetraitS)
            {
                // Retrait confirme. On rend a la source ce qu'elle etait a sa derniere
                // note, et l'on cesse de mesurer un silence qui ne dit rien d'elle.
                if (!_restaure[rang])
                {
                    _crete[rang] = _creteGardee[rang];
                    _moyenne[rang] = _moyenneGardee[rang];
                    _pique[rang] = _piqueGarde[rang];
                    _restaure[rang] = true;
                }
                _precedent[rang] = niveau;
                return;
            }
        }

        // LA CRETE DECROIT, ELLE NE SE FIGE PAS. Un maximum brut serait fixe par le premier
        // accident venu et vaudrait pour toute la soiree — c'est un piege que ce projet a
        // deja paye ailleurs. Elle suit donc ce qui monte tout de suite, et oublie
        // lentement ce qui redescend.
        _crete[rang] = niveau > _crete[rang]
            ? niveau
            : _crete[rang] * _oubli;
        _moyenne[rang] += (niveau - _moyenne[rang]) * (1f - _oubli);

        // LE PIQUE SE MESURE SUR LA PENTE, RAPPORTEE A LA CRETE.
        //
        // Rapportee, parce qu'une source jouee fort monterait sinon plus vite qu'une source
        // jouee doucement sans etre plus percussive pour autant : on mesurerait le volume du
        // disque et non la nature de l'instrument.
        //
        // Une montee qui atteint la crete en une fenetre vaut un ; en deux fenetres, un
        // demi. Au-dela de MonteeMaxS on ne parle plus d'attaque.
        // Le caractere ne s'apprend que quand elle joue : un silence ne dit rien d'elle.
        if (_crete[rang] > 1e-3f)
        {
            var tenue = Math.Clamp(_moyenne[rang] / _crete[rang], 0f, 1f);
            if (!_caractereConnu[rang]) { _caractere[rang] = tenue; _caractereConnu[rang] = true; }
            else _caractere[rang] += (tenue - _caractere[rang]) * (1f - _oubliCaractere);
        }

        var pente = niveau - _precedent[rang];
        _precedent[rang] = niveau;
        if (pente >= SeuilFrappe && niveau >= PlancherFrappe && _depuisFrappe[rang] > ReposFrappe)
        {
            _frappe[rang] = true;
            _depuisFrappe[rang] = 0;
        }
        if (pente > 0f && _crete[rang] > 1e-3f)
        {
            var parFenetre = pente / _crete[rang];
            var monte = Math.Clamp(parFenetre * (MonteeMaxS / _frameS), 0f, 1f);
            // On garde la plus franche des montees recentes plutot que leur moyenne : une
            // source qui frappe une fois par temps passe l'essentiel du temps a ne pas
            // frapper, et sa moyenne dirait « continue ».
            if (monte > _pique[rang]) _pique[rang] = monte;
        }
        _pique[rang] *= _oubli;
    }

    /// <summary>A quel point la source monte d'un coup, 0 a 1.</summary>
    public float Pique(int rang) =>
        (uint)rang < (uint)_pique.Length ? Math.Clamp(_pique[rang], 0f, 1f) : 0f;

    /// <summary>
    /// A quel point elle reste au niveau atteint, 0 a 1.
    ///
    /// C'est la moyenne rapportee a la crete — l'inverse du facteur de crete. Un souffle
    /// egal donne un ; un pizzicato, qui passe l'essentiel de son temps silencieux entre
    /// deux notes, donne peu. Aucun reglage : c'est un rapport, et il se lit tel quel.
    /// </summary>
    public float Tenue(int rang)
    {
        if ((uint)rang >= (uint)_crete.Length) return 0f;
        var c = _crete[rang];
        return c > 1e-3f ? Math.Clamp(_moyenne[rang] / c, 0f, 1f) : 0f;
    }

    /// <summary>Le caractere d'un rang et s'il est connu, pour l'empreinte.</summary>
    public (float Caractere, bool Connu) Exporter(int rang) =>
        (uint)rang < (uint)_caractere.Length ? (_caractere[rang], _caractereConnu[rang]) : (0.5f, false);

    /// <summary>Pose un caractere appris ailleurs. Le reste de la mesure repart de zero : c'est un autre signal.</summary>
    public void Importer(int rang, float caractere, bool connu)
    {
        if ((uint)rang >= (uint)_caractere.Length) return;
        Vider(rang);
        _caractere[rang] = caractere;
        _caractereConnu[rang] = connu;
    }

    public void Vider(int rang)
    {
        if ((uint)rang >= (uint)_caractere.Length) return;
        _crete[rang] = _moyenne[rang] = _precedent[rang] = _pique[rang] = _muet[rang] = 0f;
        _frappe[rang] = false; _depuisFrappe[rang] = 0;
        _caractere[rang] = 0f; _caractereConnu[rang] = false;
        _creteGardee[rang] = _moyenneGardee[rang] = _piqueGarde[rang] = 0f; _restaure[rang] = false;
    }

    /// <summary>Deplace l'etat de chaque rang selon le relais (voir <see cref="MotifSources.Reorganiser"/>).</summary>
    public void Reorganiser(ReadOnlySpan<int> ancienRang)
    {
        var n = _caractere.Length;
        var copie = new SourceEnvelope(n, _frameS) { _tempsMs = _tempsMs };
        for (var r = 0; r < n; r++)
        {
            var a = r < ancienRang.Length ? ancienRang[r] : -1;
            if (a < 0 || a >= n) continue;
            copie._crete[r] = _crete[a]; copie._moyenne[r] = _moyenne[a]; copie._precedent[r] = _precedent[a];
            copie._pique[r] = _pique[a]; copie._muet[r] = _muet[a]; copie._frappe[r] = _frappe[a];
            copie._depuisFrappe[r] = _depuisFrappe[a]; copie._caractere[r] = _caractere[a];
            copie._caractereConnu[r] = _caractereConnu[a]; copie._creteGardee[r] = _creteGardee[a];
            copie._moyenneGardee[r] = _moyenneGardee[a]; copie._piqueGarde[r] = _piqueGarde[a]; copie._restaure[r] = _restaure[a];
        }
        Array.Copy(copie._crete, _crete, n); Array.Copy(copie._moyenne, _moyenne, n); Array.Copy(copie._precedent, _precedent, n);
        Array.Copy(copie._pique, _pique, n); Array.Copy(copie._muet, _muet, n); Array.Copy(copie._frappe, _frappe, n);
        Array.Copy(copie._depuisFrappe, _depuisFrappe, n); Array.Copy(copie._caractere, _caractere, n);
        Array.Copy(copie._caractereConnu, _caractereConnu, n); Array.Copy(copie._creteGardee, _creteGardee, n);
        Array.Copy(copie._moyenneGardee, _moyenneGardee, n); Array.Copy(copie._piqueGarde, _piqueGarde, n); Array.Copy(copie._restaure, _restaure, n);
    }

    /// <summary>Oublie tout : changement de disque.</summary>
    public void Reset()
    {
        Array.Clear(_crete);
        Array.Clear(_moyenne);
        Array.Clear(_precedent);
        Array.Clear(_pique);
        Array.Clear(_muet);
        Array.Clear(_frappe);
        Array.Clear(_depuisFrappe);
        Array.Clear(_caractere);
        Array.Clear(_caractereConnu);
        Array.Clear(_creteGardee);
        Array.Clear(_moyenneGardee);
        Array.Clear(_piqueGarde);
        Array.Clear(_restaure);
    }

    /// <summary>Depuis combien de secondes la source s'est retiree. Zero si elle joue.</summary>
    public float Muet(int rang) =>
        (uint)rang < (uint)_muet.Length ? _muet[rang] : 0f;
}
