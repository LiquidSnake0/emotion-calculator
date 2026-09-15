using Emotion.Signal;

namespace Emotion.Signal.Tests;

/// <summary>
/// LE RELAIS : le cue apprend, le master joue — et pendant le fondu, le master suit les
/// deux disques a la fois.
///
/// > « L'analyse du cue et l'analyse du master, c'est deux choses qui s'executent en
/// >   parallele. L'un fixe les regles, l'autre analyse en temps reel avec deja toutes
/// >   les informations necessaires. Si j'enleve les basses de A, le master saura que les
/// >   basses de A ne sont pas la ; on aura le tchak cote A et la basse cote B qui tournent
/// >   en meme temps. »
///
/// On fabrique donc deux disques dont on connait les instruments, on fait apprendre chacun
/// par un cue, et l'on regarde si un master qui n'a rien appris retrouve, dans leur melange
/// qui bascule de l'un a l'autre, ce qui appartient a chacun.
/// </summary>
public class RelaisTests
{
    private const int Rate = 16_000;
    private const int Hop = 512;

    private sealed record Instrument(float GraveHz, float[] Harmoniques);

    private static readonly Instrument Basse = new(55f, [1f, 0.5f, 0.2f, 0.08f]);
    private static readonly Instrument Piano = new(220f, [1f, 0.3f, 0.6f, 0.15f, 0.25f, 0.05f, 0.1f]);
    private static readonly Instrument Clair = new(880f, [0.6f, 0.9f, 1f, 0.9f, 0.7f]);
    private static readonly Instrument Nappe = new(110f, [0.4f, 1f, 0.2f, 0.9f, 0.1f, 0.5f]);

    /// <summary>
    /// Un disque fabrique : ses instruments changent de note a des moments differents, avec
    /// une graine propre pour que deux disques ne se ressemblent pas.
    /// </summary>
    private sealed class Disque
    {
        private readonly IReadOnlyList<Instrument> _instruments;
        private readonly Random _alea;
        private readonly double[][] _phases;
        private readonly float[] _hz, _gain;
        private readonly int[] _reste;

        public Disque(IReadOnlyList<Instrument> instruments, int graine)
        {
            _instruments = instruments;
            _alea = new Random(graine);
            var n = instruments.Count;
            _phases = new double[n][];
            _hz = new float[n]; _gain = new float[n]; _reste = new int[n];
            for (var i = 0; i < n; i++) _phases[i] = new double[instruments[i].Harmoniques.Length];
        }

        /// <summary>Le niveau de chaque instrument sur le dernier bloc rendu.</summary>
        public float[] Niveaux => (float[])_gain.Clone();

        /// <summary>Ajoute un bloc du disque a <paramref name="bloc"/>, au volume donne.</summary>
        public void Jouer(float[] bloc, float volume)
        {
            for (var i = 0; i < _instruments.Count; i++)
            {
                if (_reste[i]-- > 0) continue;
                var demiTon = _alea.Next(0, 12);
                _hz[i] = _instruments[i].GraveHz * MathF.Pow(2f, demiTon / 12f);
                _gain[i] = 0.3f + 0.7f * (float)_alea.NextDouble();
                _reste[i] = _alea.Next(6, 20);
            }
            for (var i = 0; i < _instruments.Count; i++)
            {
                var harm = _instruments[i].Harmoniques;
                for (var k = 0; k < harm.Length; k++)
                {
                    var pas = 2 * Math.PI * _hz[i] * (k + 1) / Rate;
                    var ph = _phases[i][k];
                    var a = volume * _gain[i] * harm[k] * 0.1f;
                    for (var j = 0; j < bloc.Length; j++)
                    {
                        bloc[j] += a * (float)Math.Sin(ph);
                        ph += pas;
                    }
                    _phases[i][k] = ph % (2 * Math.PI);
                }
            }
        }
    }

    private static SourceSeparator Cue(Disque disque, int memoire = 300)
    {
        var sep = new SourceSeparator(Rate, Hop, memoire) { ApprentissageEnLigne = true };
        var bloc = new float[Hop];
        for (var t = 0; t < 2 * memoire + 16; t++)
        {
            Array.Clear(bloc);
            disque.Jouer(bloc, 1f);
            sep.Feed(bloc);
        }
        Assert.True(sep.Pret && sep.ChoixFait, "le cue n'a pas appris");
        return sep;
    }

    private static float Correlation(IReadOnlyList<float> a, IReadOnlyList<float> b)
    {
        double ma = a.Average(), mb = b.Average(), sab = 0, saa = 0, sbb = 0;
        for (var i = 0; i < a.Count; i++)
        {
            var da = a[i] - ma; var db = b[i] - mb;
            sab += da * db; saa += da * da; sbb += db * db;
        }
        return saa > 1e-12 && sbb > 1e-12 ? (float)(sab / Math.Sqrt(saa * sbb)) : 0f;
    }

    [Fact]
    public void Un_master_qui_adopte_le_portrait_suit_comme_le_cue()
    {
        var disque = new Disque([Basse, Piano], 11);
        var cue = Cue(disque);
        Assert.Equal(2, cue.Actives);

        // Le master n'a rien appris : il recoit le portrait et suit.
        var master = new SourceSeparator(Rate, Hop, 300) { SuiviSeul = true };
        var portrait = cue.Portrait();
        master.AdopterPortrait(portrait);
        Assert.True(master.Pret);
        Assert.True(master.Verrou);
        Assert.Equal(cue.Actives, master.Actives);

        // Le meme son aux deux : les niveaux suivis doivent se ressembler rang par rang.
        var bloc = new float[Hop];
        var a0 = new List<float>(); var b0 = new List<float>();
        var a1 = new List<float>(); var b1 = new List<float>();
        for (var t = 0; t < 300; t++)
        {
            Array.Clear(bloc);
            disque.Jouer(bloc, 1f);
            cue.Feed(bloc); master.Feed(bloc);
            if (t < 40) continue;   // le temps que les deux suivis se posent
            a0.Add(cue.ActivationOrdonnee(0)); b0.Add(master.ActivationOrdonnee(0));
            a1.Add(cue.ActivationOrdonnee(1)); b1.Add(master.ActivationOrdonnee(1));
        }
        Assert.True(Correlation(a0, b0) > 0.9f, $"rang 0 : {Correlation(a0, b0):F2}");
        Assert.True(Correlation(a1, b1) > 0.9f, $"rang 1 : {Correlation(a1, b1):F2}");
        // Et le master n'a pas appris : ses gabarits sont ceux recus, a l'octet pres — le
        // cue, lui, a pu continuer a reapprendre entre-temps.
        Assert.Equal(portrait.Gabarits, master.Portrait().Gabarits);
    }

    [Fact]
    public void Pendant_le_fondu_le_master_suit_les_deux_disques()
    {
        // Deux disques qui ne partagent aucun instrument : la basse et le piano d'un cote,
        // une nappe et un son clair de l'autre.
        var disqueA = new Disque([Basse, Piano], 21);
        var disqueB = new Disque([Nappe, Clair], 22);
        var cueA = Cue(disqueA);
        var cueB = Cue(disqueB);
        var kA = cueA.Actives; var kB = Math.Min(cueB.Actives, SourceSeparator.ParDisqueEnFondu);
        Assert.InRange(kA, 2, 4);
        Assert.InRange(kB, 2, 4);

        var master = new SourceSeparator(Rate, Hop, 300) { SuiviSeul = true };
        master.AdopterPortrait(cueA.Portrait());
        var relais = master.AccueillirPortrait(cueB.Portrait());
        Assert.Equal(kA + kB, master.Actives);
        Assert.Equal(kA + kB, relais.Actives);
        Assert.True(master.EnFondu);
        for (var r = 0; r < kA; r++) Assert.Equal(0, master.DisqueOrdonne(r));
        for (var r = kA; r < kA + kB; r++) Assert.Equal(1, master.DisqueOrdonne(r));
        Assert.Equal(2, master.DisqueOrdonne(master.RangReste));

        // Le fondu : A descend, B monte, sur trois cents images.
        var bloc = new float[Hop];
        var volA = new List<float>(); var volB = new List<float>();
        var suiviA = new List<float>(); var suiviB = new List<float>();
        const int n = 300;
        for (var t = 0; t < n; t++)
        {
            var part = t / (float)(n - 1);
            var vA = 1f - part; var vB = part;
            Array.Clear(bloc);
            disqueA.Jouer(bloc, vA);
            disqueB.Jouer(bloc, vB);
            master.Feed(bloc);
            if (t < 20) continue;
            volA.Add(vA); volB.Add(vB);
            float sa = 0, sb = 0;
            for (var r = 0; r < kA; r++) sa += master.ActivationOrdonnee(r);
            for (var r = kA; r < kA + kB; r++) sb += master.ActivationOrdonnee(r);
            suiviA.Add(sa); suiviB.Add(sb);
        }
        // Ce qui est a A doit descendre avec A, ce qui est a B monter avec B.
        var cA = Correlation(volA, suiviA); var cB = Correlation(volB, suiviB);
        Assert.True(cA > 0.7f, $"les sources de A ne suivent pas le fader de A : {cA:F2}");
        Assert.True(cB > 0.7f, $"les sources de B ne suivent pas le fader de B : {cB:F2}");
        // Et a la fin, quand A s'est tu, ses cases ont fondu — a la fuite pres : deux
        // instruments qui partagent des raies laissent passer une part de l'un dans l'autre,
        // mesuree ailleurs a 0,4 sur du vrai son. Ce qui compte, c'est que les cases de A
        // aient suivi le fader de A : un tiers de ce qu'elles montraient au debut, au plus.
        var debutA = suiviA.Take(20).Average(); var finA = suiviA.TakeLast(20).Average();
        var debutB = suiviB.Take(20).Average(); var finB = suiviB.TakeLast(20).Average();
        Assert.True(finA < 0.34f * debutA, $"les cases de A n'ont pas suivi le fader : {debutA:F1} → {finA:F1}");
        Assert.True(finB > 3f * debutB, $"les cases de B n'ont pas suivi le fader : {debutB:F1} → {finB:F1}");
        Assert.True(finA < 0.5f * finB, $"a la fin, A devrait etre bien sous B : A {finA:F1}, B {finB:F1}");
    }

    [Fact]
    public void Le_retrait_garde_le_disque_entrant_et_le_retague()
    {
        var cueA = Cue(new Disque([Basse, Piano], 31));
        var cueB = Cue(new Disque([Nappe, Clair], 32));
        var master = new SourceSeparator(Rate, Hop, 300) { SuiviSeul = true };
        var kA = cueA.Actives;
        var portraitB = cueB.Portrait();
        var kB = Math.Min(portraitB.Actives, SourceSeparator.ParDisqueEnFondu);
        master.AdopterPortrait(cueA.Portrait());
        master.AccueillirPortrait(portraitB);

        var relais = master.RetirerDisque(0);
        Assert.Equal(kB, master.Actives);
        Assert.False(master.EnFondu);
        for (var r = 0; r < kB; r++)
        {
            Assert.Equal(0, master.DisqueOrdonne(r));
            // Les rangs de B sont remontes en tete, dans leur ordre.
            Assert.Equal(kA + r, relais.AncienRang[r]);
        }
        // Et ce sont bien les gabarits de B (les kB plus entendus, dans leur ordre).
        if (kB == portraitB.Actives) Assert.Equal(portraitB.Gabarits, master.Portrait().Gabarits);
    }

    [Fact]
    public void Chaque_disque_garde_au_plus_quatre_gabarits_en_fondu()
    {
        // Une empreinte fabriquee a six gabarits pour A et six pour B : huit cases, dont une
        // pour le reste, donc sept gabarits au plus — quatre a B, trois a A.
        var e6 = EmpreinteFabriquee(6, 0f);
        var master = new SourceSeparator(Rate, Hop, 300) { SuiviSeul = true };
        master.AdopterPortrait(e6);
        Assert.Equal(6, master.Actives);
        var relais = master.AccueillirPortrait(EmpreinteFabriquee(6, 0f));
        Assert.Equal(7, master.Actives);
        Assert.Equal(7, relais.Actives);
        Assert.Equal(7, master.RangReste);
        var deA = Enumerable.Range(0, 7).Count(r => master.DisqueOrdonne(r) == 0);
        var deB = Enumerable.Range(0, 7).Count(r => master.DisqueOrdonne(r) == 1);
        Assert.Equal(3, deA);
        Assert.Equal(4, deB);
    }

    [Fact]
    public void Le_suivi_seul_ne_forme_jamais_de_portrait()
    {
        var master = new SourceSeparator(Rate, Hop, 300) { SuiviSeul = true, ApprentissageEnLigne = true };
        var disque = new Disque([Basse, Piano], 41);
        var bloc = new float[Hop];
        for (var t = 0; t < 700; t++) { Array.Clear(bloc); disque.Jouer(bloc, 1f); master.Feed(bloc); }
        Assert.False(master.Pret);
        Assert.Equal(0, master.Actives);
        Assert.Equal(0, master.Apprentissages);
    }

    [Fact]
    public void Le_rythme_se_verrouille_apres_trois_mesures_les_sonorites_apres_seize()
    {
        var motifs = new MotifSources(SourceSeparator.Sources);
        const int reste = 2;
        // Un boom-tchak franc sur les cases 0 et 8, mesure apres mesure.
        for (var m = 0; m < 5; m++)
            for (var c = 0; c < MotifSources.Cases; c++)
            {
                var niveau = c is 0 or 8 ? 1f : 0f;
                motifs.Feed(reste, niveau, (c + 0.5f) / MotifSources.Cases);
                motifs.Feed(0, niveau * 0.5f, (c + 0.5f) / MotifSources.Cases);
            }
        Assert.True(motifs.VerrouRythme(reste), "trois mesures de boom-tchak devraient suffire au rythme");
        Assert.False(motifs.Verrouille(3), "les sonorites, elles, attendent seize mesures");
    }

    [Fact]
    public void Le_paquet_porte_le_disque_et_les_deux_verrous()
    {
        var p = new GpuPacket();
        p.WriteSource(0, new LaneState(0.5f, 0.5f, true, Dominance: 1f, Disque: 1));
        p.WriteSource(1, new LaneState(0.5f, 0.5f, false, Dominance: 0f, Disque: 2));
        var s0 = p.ReadSource(0); var s1 = p.ReadSource(1);
        Assert.True(s0.Hit);
        Assert.False(s1.Hit);
        var octets = System.Runtime.InteropServices.MemoryMarshal.AsBytes(
            System.Runtime.InteropServices.MemoryMarshal.CreateSpan(ref p, 1)).ToArray();
        var d0 = octets[GpuPacket.SourceOffset + 0 * GpuPacket.SourceStride + GpuPacket.SourceFlags];
        var d1 = octets[GpuPacket.SourceOffset + 1 * GpuPacket.SourceStride + GpuPacket.SourceFlags];
        Assert.Equal(1, (d0 >> GpuPacket.SourceDisqueShift) & 3);
        Assert.Equal(2, (d1 >> GpuPacket.SourceDisqueShift) & 3);
        Assert.Equal(7, (d0 >> GpuPacket.SourceDominanceShift) & 7);
    }

    private static SourceSeparator.Empreinte EmpreinteFabriquee(int k, float cents)
    {
        var longueur = ProfileLearner.Longueur;
        var alea = new Random(k);
        var g = new float[k * longueur];
        for (var i = 0; i < g.Length; i++) g[i] = 0.1f + (float)alea.NextDouble();
        var vues = Enumerable.Range(0, k).Select(r => 200 - r * 10).ToArray();
        return new SourceSeparator.Empreinte(k, g, new float[k], new float[k], new float[k], new float[k], vues, cents, null);
    }
}
