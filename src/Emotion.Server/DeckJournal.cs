using Emotion.Signal;
using System.Text.Json;

namespace Emotion.Server;

/// <summary>
/// Ce que le crate a dit au moteur, et quand.
///
/// Le rendu ne connait que ce que le moteur lui ecrit dans l'anneau : c'est la regle, et
/// l'enregistrement du set (probe enregistre) la respecte. Mais pour relire une soiree il
/// faut aussi savoir ce que le DJ avait declare — quel disque au casque, quand il est
/// passe — a l'heure de la machine ET a l'horloge des images, la meme que celle des
/// paquets. Une ligne JSON par commande, dans le fichier que <c>Signal:JournalDeck</c>
/// nomme ; sans lui, rien n'est ecrit.
/// </summary>
public sealed class DeckJournal
{
    private readonly string? _chemin;
    private readonly FrameBus _bus;
    private readonly Lock _verrou = new();

    public DeckJournal(IConfiguration cfg, FrameBus bus)
    {
        _chemin = cfg["Signal:JournalDeck"];
        _bus = bus;
    }

    public void Ecrire(string verbe, object? corps = null)
    {
        if (string.IsNullOrWhiteSpace(_chemin)) return;
        var ligne = JsonSerializer.Serialize(new
        {
            heure = DateTimeOffset.Now.ToString("HH:mm:ss.fff"),
            epochMs = DateTimeOffset.Now.ToUnixTimeMilliseconds(),
            imageMs = _bus.DerniereImageMs,
            verbe,
            corps,
        });
        lock (_verrou) File.AppendAllText(_chemin, ligne + "\n");
    }
}
