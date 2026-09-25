using Emotion.Signal;
using static Emotion.Signal.MachineRelais;

namespace Emotion.Signal.Tests;

/// <summary>
/// Le relais en quatre temps, image par image. La machine ne touche a rien : on verifie
/// qu'elle rend la bonne etape au bon fondu, une fois, et surtout qu'elle se libere pour
/// le relais suivant — ce que la version enfouie dans la boucle de lecture ne faisait pas.
/// </summary>
public class MachineRelaisTests
{
    /// <summary>Un fondu lineaire de 0 a 1 en n images, puis les etapes rendues, dans l'ordre.</summary>
    private static List<Etape> Fondu(MachineRelais m, int n = 100, bool cuePret = true, bool tempo = true)
    {
        var etapes = new List<Etape>();
        for (var i = 0; i <= n; i++)
        {
            var e = m.Avancer(i / (float)n, cuePret, tempo);
            if (e != Etape.Rien) etapes.Add(e);
        }
        return etapes;
    }

    [Fact]
    public void Un_fondu_donne_accueil_tempo_retrait_dans_cet_ordre_et_une_fois()
    {
        var m = new MachineRelais();
        Assert.Equal([Etape.Accueil, Etape.Tempo, Etape.Retrait], Fondu(m));
        Assert.Equal(3, m.Phase);
        Assert.False(m.EnCours);
    }

    [Fact]
    public void Les_seuils_sont_ceux_du_relais()
    {
        var m = new MachineRelais();
        Assert.Equal(Etape.Rien, m.Avancer(0.14f, true, true));
        Assert.Equal(Etape.Accueil, m.Avancer(0.15f, true, true));
        Assert.Equal(Etape.Rien, m.Avancer(0.49f, true, true));
        Assert.Equal(Etape.Tempo, m.Avancer(0.5f, true, true));
        Assert.Equal(Etape.Rien, m.Avancer(0.89f, true, true));
        Assert.Equal(Etape.Retrait, m.Avancer(0.9f, true, true));
    }

    [Fact]
    public void Sans_portrait_au_cue_pas_d_accueil_mais_le_tempo_se_relaie()
    {
        var m = new MachineRelais();
        Assert.Equal([Etape.Tempo], Fondu(m, cuePret: false));
        Assert.Equal(0, m.Phase);
    }

    [Fact]
    public void Le_tempo_attend_d_etre_disponible()
    {
        var m = new MachineRelais();
        Assert.Equal(Etape.Accueil, m.Avancer(0.2f, true, false));
        Assert.Equal(Etape.Rien, m.Avancer(0.6f, true, false));
        Assert.Equal(Etape.Tempo, m.Avancer(0.6f, true, true));
        Assert.Equal(2, m.Phase);
    }

    /// <summary>
    /// LE DEFAUT QUI DORMAIT : apres un retrait, la machine restait en phase 3 pour toujours.
    /// Elle se libere quand le cue sort du melange, et le relais suivant se fait entier.
    /// </summary>
    [Fact]
    public void Apres_le_retrait_la_machine_se_libere_quand_le_cue_sort_du_melange_et_le_relais_suivant_est_entier()
    {
        var m = new MachineRelais();
        Fondu(m);
        Assert.Equal(3, m.Phase);

        // Le meme disque reste au casque : il est dans le melange, rien ne se libere.
        Assert.Equal(Etape.Rien, m.Avancer(0.95f, true, true));
        Assert.Equal(Etape.Rien, m.Avancer(0.5f, true, true));
        Assert.Equal(3, m.Phase);

        // Un autre disque au casque : le cue sort du melange.
        Assert.Equal(Etape.Libre, m.Avancer(0.05f, true, true));
        Assert.Equal(0, m.Phase);

        Assert.Equal([Etape.Accueil, Etape.Tempo, Etape.Retrait], Fondu(m));
    }

    [Fact]
    public void Un_relais_sans_accueil_se_libere_aussi()
    {
        var m = new MachineRelais();
        Fondu(m, cuePret: false);                        // tempo relaye, phase 0
        Assert.Equal(Etape.Rien, m.Avancer(0.95f, false, true));
        Assert.Equal(Etape.Libre, m.Avancer(0.05f, false, true));
        Assert.Equal([Etape.Accueil, Etape.Tempo, Etape.Retrait], Fondu(m));
    }

    /// <summary>
    /// Si le cue devient pret alors que son disque est deja en salle, l'accueil se fait
    /// quand meme, et le retrait suit : le master finit avec le bon portrait.
    /// </summary>
    [Fact]
    public void Un_portrait_qui_arrive_tard_est_accueilli_puis_le_retrait_suit()
    {
        var m = new MachineRelais();
        Fondu(m, cuePret: false);
        Assert.Equal(Etape.Accueil, m.Avancer(0.95f, true, true));
        Assert.Equal(Etape.Retrait, m.Avancer(0.95f, true, true));
        Assert.Equal(Etape.Libre, m.Avancer(0.05f, true, true));
    }

    /// <summary>
    /// Sans accueil, le tempo relaye suffit a dire qu'un vrai fondu est en cours : le filet
    /// du cue alternant ne doit pas le prendre pour une erreur de voie.
    /// </summary>
    [Fact]
    public void Un_tempo_relaye_sans_accueil_compte_comme_un_relais_en_cours()
    {
        var m = new MachineRelais();
        Assert.False(m.EnCours);
        m.Avancer(0.6f, false, true);
        Assert.True(m.EnCours);
        Assert.Equal(Etape.Libre, m.Avancer(0.05f, false, true));
        Assert.False(m.EnCours);
    }

    [Fact]
    public void Dix_disques_dix_relais()
    {
        var m = new MachineRelais();
        var retraits = 0;
        for (var disque = 0; disque < 10; disque++)
        {
            retraits += Fondu(m).Count(e => e == Etape.Retrait);
            m.Avancer(0f, true, true);                   // le disque suivant au casque
        }
        Assert.Equal(10, retraits);
    }

    [Fact]
    public void Un_fondu_qui_redescend_avant_le_retrait_ne_retire_rien()
    {
        var m = new MachineRelais();
        Assert.Equal(Etape.Accueil, m.Avancer(0.3f, true, true));
        Assert.Equal(Etape.Rien, m.Avancer(0.2f, true, true));
        Assert.Equal(Etape.Rien, m.Avancer(0.0f, true, true));
        Assert.True(m.EnCours);                          // le disque entrant reste accueilli
    }
}
