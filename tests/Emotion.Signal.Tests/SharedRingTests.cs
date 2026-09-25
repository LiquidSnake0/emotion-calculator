using Emotion.Signal;

namespace Emotion.Signal.Tests;

public class SharedRingTests : IDisposable
{
    /// <summary>
    /// Un serveur relance recree l'anneau a zero dans le meme fichier. Un lecteur ouvert
    /// pendant la soiree precedente doit suivre le nouveau compteur, pas attendre qu'il
    /// rattrape l'ancien.
    /// </summary>
    [Fact]
    public void Un_lecteur_suit_un_producteur_qui_repart_de_zero()
    {
        var chemin = Path.Combine(Path.GetTempPath(), "emotion-anneau-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (var ancien = new SharedRingWriter(chemin))
                for (var i = 0; i < 500; i++) { var p = new GpuPacket { Sequence = (uint)i }; ancien.Write(in p); }
            using var lecteur = new SharedRingReader(chemin);
            Assert.False(lecteur.TryRead(out _));                       // rien de neuf
            using var nouveau = new SharedRingWriter(chemin);            // repart a zero
            var neuf = new GpuPacket { Sequence = 4242 };
            nouveau.Write(in neuf);
            Assert.True(lecteur.TryRead(out var lu));
            Assert.Equal(4242u, lu.Sequence);
        }
        finally { File.Delete(chemin); }
    }

    private readonly string _path = Path.Combine(
        Path.GetTempPath(), $"emotion-ring-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try { File.Delete(_path); } catch { }
    }

    private static GpuPacket Packet(uint seq, float level = 0.5f) => new()
    {
        Magic = GpuPacket.MagicValue,
        Sequence = seq,
        TimeMs = seq * 21,
        Level = level,
    };

    [Fact]
    public void Ce_qui_est_ecrit_est_relu_a_l_identique()
    {
        using var w = new SharedRingWriter(_path, capacity: 8);
        using var r = new SharedRingReader(_path);

        w.Write(Packet(1, 0.75f));

        Assert.True(r.TryRead(out var p));
        Assert.Equal(GpuPacket.MagicValue, p.Magic);
        Assert.Equal(1u, p.Sequence);
        Assert.Equal(0.75f, p.Level);
        Assert.Equal(21, p.TimeMs);
    }

    [Fact]
    public void Rien_a_lire_quand_rien_n_a_ete_ecrit()
    {
        using var w = new SharedRingWriter(_path, capacity: 8);
        using var r = new SharedRingReader(_path);

        Assert.False(r.TryRead(out _));
    }

    [Fact]
    public void L_ordre_est_conserve()
    {
        using var w = new SharedRingWriter(_path, capacity: 64);
        using var r = new SharedRingReader(_path);

        for (uint i = 0; i < 40; i++) w.Write(Packet(i));

        for (uint i = 0; i < 40; i++)
        {
            Assert.True(r.TryRead(out var p));
            Assert.Equal(i, p.Sequence);
        }
        Assert.False(r.TryRead(out _));
    }

    [Fact]
    public void Un_lecteur_en_retard_perd_les_anciens_et_garde_les_recents()
    {
        // La regle du temps reel : le producteur n'attend jamais. Un lecteur trop lent
        // perd, et c'est voulu — une image en retard n'a aucune valeur.
        using var w = new SharedRingWriter(_path, capacity: 8);
        using var r = new SharedRingReader(_path);

        for (uint i = 0; i < 100; i++) w.Write(Packet(i));

        Assert.True(r.TryRead(out var p));
        Assert.True(p.Sequence >= 92, $"devrait repartir pres de la fin, recu {p.Sequence}");
        Assert.True(r.Missed > 0, "les pertes auraient du etre comptees");
    }

    [Fact]
    public void Un_lecteur_qui_arrive_en_cours_commence_a_l_instant_present()
    {
        // Un renderer qui se rebranche en plein set n'a que faire des cinq dernieres
        // secondes : il lui faut ce qui joue maintenant.
        using var w = new SharedRingWriter(_path, capacity: 64);
        for (uint i = 0; i < 30; i++) w.Write(Packet(i));

        using var tardif = new SharedRingReader(_path);
        Assert.False(tardif.TryRead(out _));          // rien d'ancien

        w.Write(Packet(999));
        Assert.True(tardif.TryRead(out var p));
        Assert.Equal(999u, p.Sequence);
    }

    [Fact]
    public void Le_compte_de_publications_suit()
    {
        using var w = new SharedRingWriter(_path, capacity: 8);
        Assert.Equal(0, w.Published);

        for (uint i = 0; i < 5; i++) w.Write(Packet(i));
        Assert.Equal(5, w.Published);
    }

    [Fact]
    public void Une_capacite_hors_puissance_de_deux_est_refusee()
    {
        // Le modulo est un masque de bits : sans puissance de deux, il rendrait des
        // indices faux au lieu d'echouer bruyamment.
        Assert.Throws<ArgumentException>(() => new SharedRingWriter(_path, capacity: 100));
    }

    [Fact]
    public void Ouvrir_un_fichier_qui_n_est_pas_un_anneau_echoue_clairement()
    {
        File.WriteAllBytes(_path, new byte[4096]);
        Assert.Throws<InvalidDataException>(() => new SharedRingReader(_path));
    }

    [Fact]
    public async Task Producteur_et_consommateur_concurrents_ne_voient_jamais_de_message_mixte()
    {
        // Le test qui compte vraiment. Chaque message porte une valeur derivee de sa
        // sequence ; si le lecteur voyait une case a moitie ecrite, les deux champs ne
        // correspondraient plus. C'est exactement ce que la barriere avant la
        // publication du curseur doit rendre impossible.
        using var w = new SharedRingWriter(_path, capacity: 1024);
        using var r = new SharedRingReader(_path);

        const uint total = 200_000;
        var corrompus = 0;
        var lus = 0L;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        var producteur = Task.Run(() =>
        {
            for (uint i = 1; i <= total; i++)
            {
                w.Write(new GpuPacket
                {
                    Magic = GpuPacket.MagicValue,
                    Sequence = i,
                    TimeMs = i * 7,               // lie a la sequence
                    Level = i % 1000 / 1000f,     // lie a la sequence
                });
            }
        }, cts.Token);

        var consommateur = Task.Run(() =>
        {
            while (!producteur.IsCompleted || true)
            {
                if (r.TryRead(out var p))
                {
                    lus++;
                    var attenduT = (long)p.Sequence * 7;
                    var attenduL = p.Sequence % 1000 / 1000f;

                    if (p.Magic != GpuPacket.MagicValue ||
                        p.TimeMs != attenduT ||
                        MathF.Abs(p.Level - attenduL) > 1e-6f)
                        Interlocked.Increment(ref corrompus);
                }
                else if (producteur.IsCompleted) break;
                else if (cts.IsCancellationRequested) break;
            }
        }, cts.Token);

        await Task.WhenAll(producteur, consommateur);

        Assert.True(lus > 1000, $"trop peu de messages lus : {lus}");
        Assert.Equal(0, corrompus);
    }
}
