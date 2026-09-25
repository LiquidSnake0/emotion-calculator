using Emotion.Signal;

namespace Emotion.Server;

/// <summary>
/// Les commandes venues du telephone. Quatre verbes, un par geste reel aux platines.
///
/// <b>C'EST LE SEUL RESEAU LEGITIME DU SYSTEME.</b> Une commande est rare, doit etre
/// acquittee et peut echouer : HTTP lui convient, et Crate la passe d'un simple
/// <c>fetch</c> sans embarquer de client temps reel dans la PWA. Les images, elles, ne
/// passent par aucune socket — elles sont publiees dans l'anneau partage, que l'unite de
/// rendu lit directement et lira sur PCIe ou USB-C le jour de l'eGPU.
/// </summary>
public static class DeckEndpoints
{
    public static void MapDeck(this WebApplication app)
    {
        // Cale une face au casque. N'a aucun effet sur la projection : le public ne
        // doit pas voir le beatmatch commencer.
        app.MapPost("/deck/cue", async (TrackContext track, DeckState deck, IAudioSource source,
                                        TrackMemory memory, DeckJournal journal) =>
        {
            journal.Ecrire("cue", track);
            var next = deck.Apply(d => d.Cue(track));

            // Une face arrive au casque. Si c'est la meme qu'avant — l'aiguille repasse
            // pour verifier le tempo — rien n'est remis a zero : ce passage vient s'ajouter
            // aux precedents, et c'est la que le systeme apprend le plus.
            if (Cue(source) is { } casque) memory.Cue(track, casque);

            // LA FICHE VA AU CASQUE, PAS SEULEMENT AU MASTER. C'est le cue qui apprend le
            // disque qui arrive ; sans la fiche il cherche son tempo dans le vide (43 % de
            // justesse au lieu de 99). Elle ne verrouille rien : elle dit ou chercher.
            if (source is DualAudioSource dual)
            {
                if (track.Bpm > 0f && dual.Cue is IAcceptsCue amorcable) amorcable.Amorcer(track.Bpm);
                if (Gamme.Lire(track.Camelot) is not null) dual.Cue.Analyzer?.Gamme(track.Camelot);
            }
            return Results.Ok(next);
        });

        // La transition est faite : ce qui etait cale devient ce qui joue. C'est le
        // seul geste qui change la projection.
        app.MapPost("/deck/take", async (DeckState deck,
                                         IAudioSource source, TrackMemory memory, DeckJournal journal) =>
        {
            journal.Ecrire("take");
            var next = deck.Apply(d => d.Take());

            // L'ANALYSE DOIT APPRENDRE LE CHANGEMENT DE LA BASE, PAS DU SIGNAL.
            //
            // C'est la seule chose que la fiche sait et que le son ne dira pas a temps :
            // les estimateurs oublient lentement par construction, et mettraient des
            // dizaines de secondes a admettre qu'un autre disque joue. Pendant tout ce
            // temps, le systeme annoncerait qu'il « connait » un morceau qui ne passe
            // plus.
            source.NewTrack();

            // CE QU'ON AVAIT APPRIS NE SE JETTE PAS AVEC LE DISQUE.
            //
            // NewTrack efface les estimateurs, et il le faut : ils decrivent le son qui
            // vient de s'arreter. Mais les portraits des sources et le tempo etabli
            // decrivent le disque, pas l'instant — ils sont ranges, et ceux du disque qui
            // arrive sont repris. Un disque deja passe dans la soiree recommence donc la
            // ou il s'etait arrete au lieu de tout redecouvrir.
            // CE QUE LE CASQUE A APPRIS SUIT LE DISQUE JUSQU'AUX ENCEINTES.
            //
            // NewTrack efface les estimateurs, et il le faut : ils decrivent le son qui
            // vient de s'arreter. Mais les portraits des sources et le tempo etabli
            // decrivent la face, pas l'instant — et cette face vient de passer une minute
            // au casque. Les recalculer serait payer deux fois, au seul moment ou l'on n'a
            // pas le temps.
            //
            // C'est le seul instant du systeme ou quoi que ce soit est recopie. Une
            // transition est un geste ; rien de ceci ne tourne pendant l'analyse.
            //
            // AVEC UNE VRAIE TABLE, C'EST LE RELAIS QUI TRANSPORTE — PAS CE GESTE. Le fondu
            // mesure a deja fait passer les portraits du casque au master (accueil) et retire
            // le disque sorti (retrait), au rythme du fader ; et le cue a pu changer de voie
            // depuis. Recopier ici ce que « le cue » sait rangerait la connaissance d'une
            // autre voie dans le master, et recentrerait son tempo sur un disque qui ne
            // joue plus. Le geste ne fait alors que tenir les comptes, et donner au master
            // la fiche du disque qui joue desormais.
            if (Master(source) is { } platine)
            {
                var relaisParLeSignal = source is DualAudioSource;
                memory.Handover(next.Playing, platine, relaisParLeSignal ? null : Cue(source));
                if (relaisParLeSignal && next.Playing.Bpm > 0f && platine is IAcceptsCue amorcable)
                    amorcable.Amorcer(next.Playing.Bpm);
                if (relaisParLeSignal && Gamme.Lire(next.Playing.Camelot) is not null)
                    source.Analyzer?.Gamme(next.Playing.Camelot);
            }

            return Results.Ok(next);
        });

        // Renoncement : la face calee est abandonnee.
        app.MapPost("/deck/drop", async (DeckState deck,
                                         IAudioSource source, TrackMemory memory, DeckJournal journal) =>
        {
            journal.Ecrire("drop");
            var next = deck.Apply(d => d.Drop());

            // Le vinyle est range : ce qu'on savait de lui part avec. Le garder ne servirait
            // qu'a occuper de la place pour une face qui ne reviendra pas ce soir.
            memory.Forget(cue: true, Cue(source));
            return Results.Ok(next);
        });

        // Pose directement ce qui joue, sans passer par le casque. Sert au demarrage
        // d'un set et aux essais.
        app.MapPost("/deck/play", async (TrackContext track, DeckState deck, IAudioSource source,
                                         TrackMemory memory, DeckJournal journal) =>
        {
            journal.Ecrire("play", track);
            var next = deck.Apply(_ => new Deck(track, null));
            source.NewTrack();
            if (Master(source) is { } platine) memory.Play(track, platine);
            return Results.Ok(next);
        });

        // L'AVANCE DU VISUEL N'EST PLUS UNE COMMANDE DU MOTEUR, ET C'EST JUSTE.
        //
        // Elle a longtemps ete un POST /lead, parce que le renderer etait une page servie
        // par ce processus. Le moteur ne l'appliquait pourtant jamais : il relayait le
        // chiffre au navigateur, qui seul le portait. Une commande qui traverse le reseau
        // pour ne rien faire ici est une commande de trop.
        //
        // Le calage se fait toujours depuis la piste et jamais depuis la table — le son
        // met 5,8 ms pour atteindre celui qui regle a la table et 29 pour le public a dix
        // metres, et regler de la revient a faire preceder le mur de vingt-trois
        // millisecondes pour tout le monde d'autre, un ecart plus grand que tout ce que
        // l'analyse a gagne en une soiree de mesures. Mais le reglage appartient
        // desormais a l'unite de rendu, qui est la seule a pouvoir l'appliquer : dans la
        // fenetre Qt, les touches + et - ; sur l'eGPU, son propre parametre.

        app.MapGet("/deck", (DeckState deck) => Results.Ok(deck.Current));
    }

    /// <summary>La face qui joue, si elle sait apprendre.</summary>
    private static ILearnsTracks? Master(IAudioSource source) => source switch
    {
        DualAudioSource d => d.Master as ILearnsTracks,
        ILearnsTracks l => l,
        _ => null,
    };

    /// <summary>La face calee au casque, si elle sait apprendre.</summary>
    private static ILearnsTracks? Cue(IAudioSource source) =>
        source is DualAudioSource d ? d.Cue as ILearnsTracks : null;
}
