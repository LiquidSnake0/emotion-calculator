using Emotion.Signal;

namespace Emotion.Signal.Tests;

public class FrameBusTests
{
    private static VisualFrame Frame(long t) =>
        new(t, 0.5f, new float[VisualFrame.BandCount], false, null, null);

    [Fact]
    public async Task Chaque_abonne_recoit_toutes_les_images_quand_il_suit()
    {
        var bus = new FrameBus();
        var a = bus.Subscribe("a", capacity: 64);
        var b = bus.Subscribe("b", capacity: 64);

        for (var i = 0; i < 20; i++) bus.Publish(Frame(i));
        bus.Complete();

        var ra = await Collect(a);
        var rb = await Collect(b);

        Assert.Equal(20, ra.Count);
        Assert.Equal(20, rb.Count);
        Assert.Equal(0, ra[0].T);
        Assert.Equal(19, ra[^1].T);
    }

    [Fact]
    public async Task Un_abonne_lent_ne_ralentit_pas_les_autres()
    {
        // La propriete qui justifie le bus. Sans lui, une unite de rendu occupee ferait
        // sauter le visuel web, alors que les deux n'ont rien a voir.
        var bus = new FrameBus();
        var rapide = bus.Subscribe("rapide", capacity: 64);
        var lent = bus.Subscribe("lent", capacity: 2);

        for (var i = 0; i < 50; i++) bus.Publish(Frame(i));   // le lent n'a rien lu
        bus.Complete();

        var vus = await Collect(rapide);
        Assert.Equal(50, vus.Count);            // le rapide n'a rien perdu
    }

    [Fact]
    public async Task Une_file_pleine_garde_les_images_recentes()
    {
        // On jette l'ancienne, jamais la nouvelle : en temps reel, une image en retard
        // n'a aucune valeur puisque la suivante est deja meilleure.
        var bus = new FrameBus();
        var sub = bus.Subscribe("borne", capacity: 3);

        for (var i = 0; i < 100; i++) bus.Publish(Frame(i));
        bus.Complete();

        var vus = await Collect(sub);

        Assert.True(vus.Count <= 3, $"{vus.Count} images pour une file de 3");
        Assert.Equal(99, vus[^1].T);            // la derniere publiee est bien la
    }

    [Fact]
    public void Publier_sans_abonne_ne_leve_rien()
    {
        // Au demarrage, l'analyse tourne avant que le renderer se connecte.
        var bus = new FrameBus();
        bus.Publish(Frame(0));
    }

    [Fact]
    public async Task Un_abonne_arrive_en_cours_de_route_recoit_la_suite()
    {
        // Un renderer qui redemarre en plein set ne doit pas recevoir l'historique :
        // il lui faut ce qui joue maintenant.
        var bus = new FrameBus();
        for (var i = 0; i < 10; i++) bus.Publish(Frame(i));

        var tardif = bus.Subscribe("tardif", capacity: 64);
        for (var i = 10; i < 15; i++) bus.Publish(Frame(i));
        bus.Complete();

        var vus = await Collect(tardif);
        Assert.Equal(5, vus.Count);
        Assert.Equal(10, vus[0].T);
    }

    [Fact]
    public async Task Les_pertes_sont_comptees()
    {
        // Un visuel qui saccade sans explication est indebuggable : on compte.
        var bus = new FrameBus();
        var sub = bus.Subscribe("borne", capacity: 2);

        for (var i = 0; i < 40; i++) bus.Publish(Frame(i));
        bus.Complete();
        await Collect(sub);

        Assert.True(sub.Dropped > 0, "des images auraient du etre comptees perdues");
        Assert.Contains(bus.Stats(), s => s.Name == "borne" && s.Dropped > 0);
    }

    private static async Task<List<VisualFrame>> Collect(FrameBus.Subscription sub)
    {
        var list = new List<VisualFrame>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            await foreach (var f in sub.ReadAllAsync(cts.Token)) list.Add(f);
        }
        catch (OperationCanceledException) { }
        return list;
    }
}

public class GpuPacketTests
{
    [Fact]
    public void La_taille_est_celle_annoncee()
    {
        // Le lecteur CUDA s'aligne sur cette constante : elle ne doit pas deriver du
        // cote .NET sans qu'un test le voie.
        Assert.Equal(GpuPacket.Size,
                     System.Runtime.InteropServices.Marshal.SizeOf<GpuPacket>());
        Assert.Equal(256, GpuPacket.Size);

        // Chaque source doit avoir son mot a elle : deux sources qui partagent un octet
        // se perdent des attaques des qu'elles ecrivent en meme temps.
        Assert.Equal(192,
                     GpuPacket.SourceOffset + GpuPacket.SourceSlots * GpuPacket.SourceStride);
    }

    [Fact]
    public void Le_message_porte_tout_ce_qui_pilote_le_rendu()
    {
        var bands = new float[VisualFrame.BandCount];
        for (var i = 0; i < bands.Length; i++) bands[i] = i / 11f;

        var frame = new VisualFrame(
            T: 1234, Rms: 0.7f, Bands: bands,
            Onset: true, Phase: 0.25f, Bpm: 87f,
            Hits: new Hits(Kick: true, Clap: false, Hat: true),
            Harmony: new Harmony(new float[12], Pitch: 7, Strength: 0.5f,
                                 Change: 0.3f, Tonality: 0.6f),
            Blend: 0.4f);

        var track = new TrackContext("t", "d", "B", "8A", "M+", "#154360", null);
        var p = GpuPacket.From(frame, track, sequence: 42);

        Assert.Equal(GpuPacket.MagicValue, p.Magic);
        Assert.Equal(42u, p.Sequence);
        Assert.Equal(1234, p.TimeMs);
        Assert.Equal(87f, p.Bpm);
        Assert.Equal(0.4f, p.Blend);
        Assert.Equal(7, p.Pitch);
        Assert.Equal((byte)Kind.Thunder, p.Scene);

        Assert.Equal(GpuPacket.KickBit | GpuPacket.HatBit, p.Hits);
        Assert.Equal(0x15, p.R);
        Assert.Equal(0x43, p.G);
        Assert.Equal(0x60, p.B);
        Assert.Equal(255, p.Bands[11]);   // les bandes sont des octets, 0 a 255 pour 0 a 1
    }

    [Fact]
    public void Un_tempo_non_accroche_part_a_zero()
    {
        // Le lecteur CUDA n'a pas de notion de valeur absente : zéro signifie
        // « pas encore », et c'est documenté dans le contrat.
        var frame = new VisualFrame(0, 0.1f, new float[VisualFrame.BandCount],
                                    false, null, null);
        var p = GpuPacket.From(frame, TrackContext.Silence, 0);

        Assert.Equal(0f, p.Bpm);
        Assert.Equal(0f, p.Phase);
        Assert.Equal(GpuPacket.NoPitch, p.Pitch);
    }

    [Fact]
    public void Le_message_s_ecrit_et_se_relit_octet_pour_octet()
    {
        // Ce que fera le lecteur de l'autre cote : lire la structure telle quelle depuis
        // un bloc d'octets, sans parcours ni allocation.
        var frame = new VisualFrame(99, 0.5f, new float[VisualFrame.BandCount],
                                    false, 0.5f, 90f);
        var p = GpuPacket.From(frame, TrackContext.Silence, 7);

        Span<byte> buffer = stackalloc byte[GpuPacket.Size];
        System.Runtime.InteropServices.MemoryMarshal.Write(buffer, in p);
        var back = System.Runtime.InteropServices.MemoryMarshal.Read<GpuPacket>(buffer);

        Assert.Equal(GpuPacket.MagicValue, back.Magic);
        Assert.Equal(7u, back.Sequence);
        Assert.Equal(99, back.TimeMs);
        Assert.Equal(90f, back.Bpm);
    }
}
