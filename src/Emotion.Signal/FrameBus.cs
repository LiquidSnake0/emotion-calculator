using System.Threading.Channels;

namespace Emotion.Signal;

/// <summary>
/// Distribue chaque image du signal a plusieurs consommateurs, sans qu'aucun ne puisse
/// ralentir les autres.
///
/// <b>Le composant qui manquait.</b> Tant qu'il n'y avait qu'un renderer web, le worker
/// pouvait diffuser lui-meme. Des qu'un second consommateur arrive — l'unite de rendu
/// GPU, un enregistreur de set — un seul chemin ne tient plus : le plus lent dicterait
/// la cadence du plus rapide, et une unite GPU occupee ferait sauter le visuel web.
///
/// <b>La regle, et elle est propre au temps reel :</b> quand une file deborde, on jette
/// la <i>plus ancienne</i> image, jamais la nouvelle, et on ne bloque jamais le
/// producteur. Une image de 21 ms arrivee en retard n'a aucune valeur — la suivante est
/// deja meilleure. Bloquer pour la livrer quand meme reviendrait a ajouter du retard a
/// tout le monde pour satisfaire le plus lent.
///
/// Aucun verrou sur le chemin chaud. <see cref="Channel"/> en mode un producteur et un
/// consommateur utilise des operations atomiques, et <see cref="Publish"/> ne fait que
/// des ecritures qui ne peuvent pas echouer.
/// </summary>
public sealed class FrameBus
{
    /// <summary>
    /// Un abonne : sa file, son nom, et le compte de ce qu'il a laisse passer.
    /// </summary>
    public sealed class Subscription
    {
        private readonly Channel<VisualFrame> _channel;
        private long _dropped;

        internal Subscription(string name, int capacity)
        {
            Name = name;

            _channel = Channel.CreateBounded<VisualFrame>(new BoundedChannelOptions(capacity)
            {
                // La decision de conception, en une option : on abandonne la plus
                // ancienne plutot que d'attendre.
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = true,
                AllowSynchronousContinuations = false,
            });
        }

        public string Name { get; }

        /// <summary>Images jetees faute d'avoir ete lues a temps. Diagnostic.</summary>
        public long Dropped => Volatile.Read(ref _dropped);

        /// <summary>Les images a consommer, jusqu'a l'annulation.</summary>
        public IAsyncEnumerable<VisualFrame> ReadAllAsync(CancellationToken ct)
            => _channel.Reader.ReadAllAsync(ct);

        internal void Offer(in VisualFrame frame)
        {
            // TryWrite ne peut pas echouer sur une file en mode DropOldest : elle fait
            // de la place. On compte ce qui a saute pour pouvoir le voir, plutot que de
            // decouvrir plus tard un visuel qui saccade sans explication.
            if (!_channel.Writer.TryWrite(frame))
                Interlocked.Increment(ref _dropped);
            else if (_channel.Reader.Count >= _capacity)
                Interlocked.Increment(ref _dropped);
        }

        private int _capacity;
        internal void SetCapacity(int c) => _capacity = c;

        internal void Complete() => _channel.Writer.TryComplete();
    }

    private volatile Subscription[] _subscribers = [];
    private volatile Action<VisualFrame>[] _inline = [];
    private readonly Lock _gate = new();

    /// <summary>
    /// Abonne un consommateur <b>appele en ligne</b>, dans le fil du producteur, sans
    /// passer par une file.
    ///
    /// C'est le chemin le plus court possible, et il supprime un reveil de tache : une
    /// file impose au systeme de reveiller un fil, ce qui coute plus cher que l'ecriture
    /// elle-meme quand celle-ci se compte en microsecondes.
    ///
    /// <b>A n'utiliser que pour un consommateur dont on sait qu'il ne bloque jamais.</b>
    /// L'ecriture dans l'anneau partage remplit cette condition — elle n'attend rien,
    /// n'alloue rien et ne peut pas echouer. Un consommateur qui prendrait un verrou ou
    /// ferait une entree-sortie retarderait, lui, toute la chaine.
    /// </summary>
    public void AddInlineSink(Action<VisualFrame> sink)
    {
        lock (_gate)
        {
            var next = new Action<VisualFrame>[_inline.Length + 1];
            Array.Copy(_inline, next, _inline.Length);
            next[^1] = sink;
            _inline = next;
        }
    }

    /// <summary>
    /// Abonne un consommateur.
    /// </summary>
    /// <param name="name">Nom, pour le diagnostic.</param>
    /// <param name="capacity">
    /// Profondeur de file, en images. Deux valent 42 ms de tolerance : de quoi absorber
    /// une hesitation du planificateur sans jamais laisser le visuel deriver d'une demi
    /// seconde derriere le son.
    /// </param>
    public Subscription Subscribe(string name, int capacity = 2)
    {
        var sub = new Subscription(name, capacity);
        sub.SetCapacity(capacity);

        // Recopie sous verrou, mais la lecture de Publish reste sans verrou : les
        // abonnements sont rares — quelques-uns au demarrage — alors que la publication
        // a lieu quarante-sept fois par seconde.
        lock (_gate)
        {
            var next = new Subscription[_subscribers.Length + 1];
            Array.Copy(_subscribers, next, _subscribers.Length);
            next[^1] = sub;
            _subscribers = next;
        }
        return sub;
    }

    /// <summary>
    /// Publie une image a tous les abonnes. Ne bloque jamais, n'alloue rien, ne prend
    /// aucun verrou.
    /// </summary>
    /// <summary>
    /// La derniere image publiee. Un endpoint qui interroge l'etat n'a pas besoin de
    /// s'abonner au flux pour lire une valeur courante ; ecrire une reference est atomique
    /// sur toutes les plateformes visees, donc ce raccourci ne coute rien au chemin chaud.
    /// </summary>
    public VisualFrame Latest { get; private set; }

    /// <summary>L'horloge de la derniere image publiee : celle des paquets, pour dater ce qui vient d'ailleurs.</summary>
    public long DerniereImageMs { get; private set; }

    public void Publish(in VisualFrame frame)
    {
        DerniereImageMs = frame.T;
        Latest = frame;

        // Les consommateurs en ligne d'abord : ils sont sur le chemin critique, et rien
        // ne doit s'intercaler entre l'analyse et eux.
        var inline = _inline;
        for (var i = 0; i < inline.Length; i++)
            inline[i](frame);

        var subs = _subscribers;              // une seule lecture volatile
        for (var i = 0; i < subs.Length; i++)
            subs[i].Offer(frame);
    }

    /// <summary>Clot toutes les files : les consommateurs sortent de leur boucle.</summary>
    public void Complete()
    {
        var subs = _subscribers;
        foreach (var s in subs) s.Complete();
    }

    /// <summary>Etat des abonnes, pour le diagnostic.</summary>
    public IReadOnlyList<(string Name, long Dropped)> Stats()
        => _subscribers.Select(s => (s.Name, s.Dropped)).ToArray();
}
