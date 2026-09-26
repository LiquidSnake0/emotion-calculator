using Emotion.Signal;

namespace Emotion.Signal.Tests;

/// <summary>
/// Le cue sur les voies de la table : l'alternance apres chaque relais, le filet quand la
/// voie prise pour le cue est en fait le master, et le flux qui ne laisse passer que la
/// voie active.
/// </summary>
public class CueAlternantTests
{
    /// <summary>Une voie fabriquee : des images marquees de son numero, sans analyseur.</summary>
    private sealed class Voie(int numero) : IAudioSource, ILearnsTracks, IAcceptsCue
    {
        public int NewTracks;
        public float? Amorce;
        public string Name => $"voie {numero}";
        public void NewTrack() => NewTracks++;
        public void Amorcer(float bpm) => Amorce = bpm;
        public void Resume(in TrackKnowledge knowledge) { }
        public TrackKnowledge Park(string id, in TrackKnowledge previous) => previous;

        public async IAsyncEnumerable<VisualFrame> ReadAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            for (var t = 0L; !ct.IsCancellationRequested; t += 21)
            {
                yield return new VisualFrame(t, numero, new float[VisualFrame.BandCount], false, null, null);
                await Task.Delay(5, ct);
            }
        }
    }

    private static (CueAlternant cue, Voie v1, Voie v2) Faire()
    {
        var v1 = new Voie(1); var v2 = new Voie(2);
        return (new CueAlternant(v1, v2), v1, v2);
    }

    [Fact]
    public void Le_cue_commence_sur_la_voie_1()
    {
        var (cue, _, _) = Faire();
        Assert.Equal(1, cue.Voie);
        Assert.Equal(0, cue.Bascules);
    }

    [Fact]
    public void Apres_le_relais_le_cue_passe_sur_l_autre_voie_et_seul_ce_nouvel_analyseur_repart()
    {
        var (cue, v1, v2) = Faire();
        cue.NewTrack();
        Assert.Equal(2, cue.Voie);
        Assert.Equal(1, v2.NewTracks);
        Assert.Equal(0, v1.NewTracks);
        cue.NewTrack();
        Assert.Equal(1, cue.Voie);
        Assert.Equal(1, v1.NewTracks);
    }

    [Fact]
    public void La_fiche_va_a_la_voie_active()
    {
        var (cue, v1, v2) = Faire();
        cue.Amorcer(88f);
        Assert.Equal(88f, v1.Amorce);
        Assert.Null(v2.Amorce);
        cue.NewTrack();
        cue.Amorcer(92f);
        Assert.Equal(92f, v2.Amorce);
    }

    [Fact]
    public void Le_filet_permute_apres_trois_secondes_dans_le_master_hors_relais()
    {
        var (cue, _, _) = Faire();
        for (var i = 0; i < CueAlternant.ImagesDedans - 1; i++)
            Assert.False(cue.Observer(0.95f, relaisEnCours: false));
        Assert.True(cue.Observer(0.95f, relaisEnCours: false));
        Assert.Equal(2, cue.Voie);
    }

    [Fact]
    public void Le_filet_ne_permute_jamais_pendant_un_relais()
    {
        var (cue, _, _) = Faire();
        for (var i = 0; i < 10 * CueAlternant.ImagesDedans; i++)
            Assert.False(cue.Observer(0.95f, relaisEnCours: true));
        Assert.Equal(1, cue.Voie);
    }

    [Fact]
    public void Un_fondu_qui_monte_ne_declenche_pas_le_filet()
    {
        // Un vrai fondu monte depuis zero : sous 0,8 le compte reste a zero, et au-dessus
        // le relais est engage. Ici, sans relais engage, seules les images a 0,8 comptent.
        var (cue, _, _) = Faire();
        for (var i = 0; i < 400; i++)
            Assert.False(cue.Observer(i / 400f * 0.79f, relaisEnCours: false));
        Assert.Equal(1, cue.Voie);
    }

    [Fact]
    public void Une_baisse_remet_le_compte_du_filet_a_zero()
    {
        var (cue, _, _) = Faire();
        for (var i = 0; i < CueAlternant.ImagesDedans - 1; i++) cue.Observer(0.95f, false);
        cue.Observer(0.3f, false);
        for (var i = 0; i < CueAlternant.ImagesDedans - 1; i++)
            Assert.False(cue.Observer(0.95f, false));
        Assert.Equal(1, cue.Voie);
    }

    /// <summary>
    /// La voie du cue muette cinq secondes pendant que l'autre joue : le cue change de voie,
    /// meme en plein relais — la parite de l'alternance s'etait perdue.
    /// </summary>
    [Fact]
    public void Une_voie_muette_pendant_que_l_autre_joue_n_est_pas_le_cue()
    {
        var (cue, _, _) = Faire();
        cue.Niveau(1, 0f); cue.Niveau(2, 0.3f);
        for (var i = 0; i < CueAlternant.ImagesMuettes - 1; i++)
            Assert.False(cue.Observer(0f, relaisEnCours: true));
        Assert.True(cue.Observer(0f, relaisEnCours: true));
        Assert.Equal(2, cue.Voie);
    }

    [Fact]
    public void Deux_voies_muettes_ne_font_pas_basculer()
    {
        var (cue, _, _) = Faire();
        cue.Niveau(1, 0f); cue.Niveau(2, 0f);
        for (var i = 0; i < 3 * CueAlternant.ImagesMuettes; i++)
            Assert.False(cue.Observer(0f, relaisEnCours: false));
        Assert.Equal(1, cue.Voie);
    }

    [Fact]
    public async Task Le_flux_ne_porte_que_la_voie_active_et_suit_la_bascule()
    {
        var (cue, _, _) = Faire();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var avant = new List<float>(); var apres = new List<float>();
        await foreach (var f in cue.ReadAsync(stop.Token))
        {
            if (cue.Voie == 1) { avant.Add(f.Rms); if (avant.Count == 20) cue.Basculer("test"); }
            else { apres.Add(f.Rms); if (apres.Count >= 30) break; }
        }
        Assert.All(avant, r => Assert.Equal(1f, r));
        Assert.True(apres.Count >= 20);
        // Les premieres images apres la bascule peuvent encore venir de la voie 1 : huit deja
        // dans la file, et une que sa tache avait lue juste avant de voir la bascule ; ensuite,
        // il n'y a plus que la 2.
        Assert.All(apres.Skip(10), r => Assert.Equal(2f, r));
    }
}
