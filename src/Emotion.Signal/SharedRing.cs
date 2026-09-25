using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;

namespace Emotion.Signal;

/// <summary>
/// Anneau en memoire partagee, un producteur et un consommateur, sans verrou.
///
/// C'est le tuyau vers l'unite de rendu externe. Un socket coute dix a vingt
/// microsecondes par message, en appels systeme et en copies a travers le noyau ; ici
/// les deux processus ecrivent et lisent la <b>meme page physique</b>, et le cout tombe
/// a quelques centaines de nanosecondes. A quarante-sept messages par seconde la
/// difference ne se voit pas sur une moyenne, mais elle se voit sur la queue de
/// distribution — et c'est la queue qui fait sauter une image.
///
/// <b>Le producteur n'attend jamais.</b> Quand le consommateur prend du retard, on
/// ecrase : une image de vingt-et-une millisecondes arrivee en retard n'a aucune valeur,
/// la suivante est deja meilleure. Le consommateur s'en apercoit par un saut du numero
/// de sequence, ce qui est une information utile plutot qu'une panne.
///
/// <b>L'ordre des ecritures est tout.</b> On ecrit la case, puis seulement ensuite on
/// avance le curseur, avec une barriere entre les deux. Sans cette barriere, le
/// processeur ou le compilateur seraient libres de publier le curseur avant la donnee,
/// et le lecteur verrait une case a moitie ecrite — un message dont la moitie appartient
/// a l'image precedente. C'est le genre de defaut qui n'apparait qu'une fois sur mille
/// et jamais sur la machine de celui qui l'a ecrit.
/// </summary>
public static class SharedRing
{
    /// <summary>Nombre magique de l'en-tete, pour reconnaitre un anneau valide.</summary>
    public const uint Magic = 0x454D5230;      // "EMR0"

    /// <summary>
    /// Taille de l'en-tete. Une ligne de cache entiere par curseur : sans cet
    /// espacement, le curseur d'ecriture et celui de lecture partageraient la meme
    /// ligne, et chaque ecriture de l'un invaliderait le cache de l'autre. C'est le
    /// faux partage, et il coute plus cher qu'un verrou bien place.
    /// </summary>
    public const int HeaderSize = 192;

    private const int OffMagic = 0;
    private const int OffCapacity = 4;
    private const int OffSlotSize = 8;
    private const int OffWrite = 64;           // sa propre ligne de cache
    private const int OffRead = 128;           // la sienne aussi

    /// <summary>Chemin par defaut. Sur Linux, un fichier de /dev/shm est en memoire vive.</summary>
    public const string DefaultPath = "/dev/shm/emotion-emulator";

    public static long TotalSize(int capacity, int slotSize) =>
        HeaderSize + (long)capacity * slotSize;
}

/// <summary>Cote producteur de l'anneau. Un seul par anneau.</summary>
public sealed unsafe class SharedRingWriter : IDisposable
{
    private readonly MemoryMappedFile _file;
    private readonly MemoryMappedViewAccessor _view;
    private readonly byte* _base;
    private readonly int _capacity;
    private readonly int _slotSize;

    /// <param name="capacity">
    /// Nombre de cases. Puissance de deux, pour que le modulo soit un masque.
    ///
    /// Deux cent cinquante-six cases valent cinq secondes de signal : bien plus que
    /// necessaire, mais l'anneau ne coute que 24 Ko et une marge large evite d'ecraser
    /// des qu'un ordonnanceur hesite.
    /// </param>
    public SharedRingWriter(string path = SharedRing.DefaultPath,
                            int capacity = 256,
                            int slotSize = GpuPacket.Size)
    {
        if ((capacity & (capacity - 1)) != 0)
            throw new ArgumentException("puissance de deux attendue", nameof(capacity));

        _capacity = capacity;
        _slotSize = slotSize;

        var total = SharedRing.TotalSize(capacity, slotSize);
        _file = MemoryMappedFile.CreateFromFile(path, FileMode.Create, null, total);
        _view = _file.CreateViewAccessor(0, total, MemoryMappedFileAccess.ReadWrite);

        byte* p = null;
        _view.SafeMemoryMappedViewHandle.AcquirePointer(ref p);
        _base = p;

        // L'en-tete se pose avant tout, et le nombre magique en dernier : un lecteur qui
        // ouvrirait l'anneau pendant sa creation verrait alors soit rien de valide, soit
        // un en-tete complet, jamais un etat intermediaire.
        Write32(SharedRing.HeaderSize + 0, 0);
        Write32(4, capacity);
        Write32(8, slotSize);
        Write64(64, 0);
        Write64(128, 0);
        // On touche toutes les pages maintenant plutot qu'a la premiere ecriture. Une
        // memoire mappee n'est materialisee qu'au premier acces : sans cette mise en
        // place, le defaut de page tomberait pendant le set. La mesure le montrait —
        // 7,5 ms sur le pire message, contre 22 microsecondes en moyenne.
        for (long off = SharedRing.HeaderSize; off < total; off += 4096)
            *(_base + off) = 0;

        Volatile.Write(ref *(uint*)(_base + 0), SharedRing.Magic);

        Path = path;
    }

    public string Path { get; }

    /// <summary>Nombre de messages publies depuis l'ouverture.</summary>
    public long Published => Volatile.Read(ref *(long*)(_base + 64));

    /// <summary>
    /// Publie un message. Ne bloque jamais, n'alloue rien, n'echoue pas.
    /// </summary>
    public void Write(in GpuPacket packet)
    {
        var w = *(long*)(_base + 64);                       // lu sans barriere : seul ce
                                                            // fil ecrit ce curseur
        var slot = _base + SharedRing.HeaderSize + (w & (_capacity - 1)) * _slotSize;

        // 1. La donnee.
        *(GpuPacket*)slot = packet;

        // 2. La barriere, puis le curseur. Cet ordre est la seule chose qui garantit
        //    qu'un lecteur ne verra jamais une case a moitie ecrite.
        Volatile.Write(ref *(long*)(_base + 64), w + 1);
    }

    private void Write32(int offset, int value) => *(int*)(_base + offset) = value;
    private void Write64(int offset, long value) => *(long*)(_base + offset) = value;

    public void Dispose()
    {
        _view.SafeMemoryMappedViewHandle.ReleasePointer();
        _view.Dispose();
        _file.Dispose();
    }
}

/// <summary>
/// Cote consommateur. Un seul par anneau. C'est ce que le lecteur CUDA reproduira, avec
/// un simple <c>mmap</c> et la meme disposition.
/// </summary>
public sealed unsafe class SharedRingReader : IDisposable
{
    private readonly MemoryMappedFile _file;
    private readonly MemoryMappedViewAccessor _view;
    private readonly byte* _base;
    private readonly int _capacity;
    private readonly int _slotSize;
    private long _read;

    public SharedRingReader(string path = SharedRing.DefaultPath)
    {
        _file = MemoryMappedFile.CreateFromFile(path, FileMode.Open, null, 0,
                                                MemoryMappedFileAccess.ReadWrite);
        _view = _file.CreateViewAccessor(0, 0, MemoryMappedFileAccess.ReadWrite);

        byte* p = null;
        _view.SafeMemoryMappedViewHandle.AcquirePointer(ref p);
        _base = p;

        var magic = Volatile.Read(ref *(uint*)(_base + 0));
        if (magic != SharedRing.Magic)
            throw new InvalidDataException($"anneau invalide : magie 0x{magic:X8}");

        _capacity = *(int*)(_base + 4);
        _slotSize = *(int*)(_base + 8);

        // On demarre sur ce qui joue maintenant, pas sur l'historique : un renderer qui
        // se rebranche en plein set n'a que faire des cinq dernieres secondes.
        _read = Volatile.Read(ref *(long*)(_base + 64));
    }

    /// <summary>Messages ecrases avant d'avoir ete lus, depuis l'ouverture.</summary>
    public long Missed { get; private set; }

    /// <summary>
    /// Lit le prochain message s'il y en a un.
    ///
    /// Quand le producteur a pris plus d'un tour d'avance, on saute directement aux
    /// messages encore valides : relire des cases deja ecrasees rendrait des donnees
    /// melangees, ce qui est pire que de les avoir perdues.
    /// </summary>
    public bool TryRead(out GpuPacket packet)
    {
        while (true)
        {
            var w = Volatile.Read(ref *(long*)(_base + 64));

            // UNE CASE N'EST SURE QUE SI L'ECART EST STRICTEMENT INFERIEUR A LA CAPACITE.
            //
            // A l'egalite exacte, le producteur ecrit deja dans cette case : le curseur
            // vaut w, il redige donc la case w & masque, qui est precisement la notre
            // puisque w - lecture vaut un tour entier. Le curseur ne sera incremente
            // qu'apres. Une comparaison stricte laissait passer ce cas-la, et le test de
            // concurrence rendait des messages mixtes de facon intermittente.
            if (w - _read >= _capacity)
            {
                Missed += w - _read - _capacity + 1;
                _read = w - _capacity + 1;
            }

            // LE PRODUCTEUR A REDEMARRE. Un serveur relance recree l'anneau et repart a zero
            // dans le meme fichier ; un lecteur ouvert sur l'ancien compteur attendrait que le
            // nouveau le rattrape — toute une soiree. On reprend depuis le debut de ce qu'il
            // a ecrit, ou depuis le plus ancien encore valide.
            if (w < _read) _read = w < _capacity ? 0 : w - _capacity + 1;

            if (_read >= w)
            {
                packet = default;
                return false;
            }

            var slot = _base + SharedRing.HeaderSize + (_read & (_capacity - 1)) * _slotSize;
            packet = *(GpuPacket*)slot;

            // VERIFICATION APRES LECTURE, et c'est indispensable.
            //
            // Constater qu'une case est valide puis la lire ne suffit pas : entre les
            // deux, le producteur a pu faire un tour complet et la reecrire pendant
            // qu'on la copiait. On obtient alors un message dont la moitie appartient a
            // une image et la moitie a une autre.
            //
            // Le defaut existait depuis le debut mais restait improbable ; il est apparu
            // le jour ou le message est passe de 96 a 112 octets, parce qu'une copie plus
            // longue elargit la fenetre de course. C'est exactement le genre de bogue
            // qu'un test de concurrence existe pour attraper — dix-sept messages mixtes
            // sur deux cent mille.
            //
            // On relit donc le curseur : si la case qu'on vient de copier a ete rejointe
            // entre-temps, la copie est suspecte et on recommence a un point sur. Meme
            // borne qu'a l'entree, et pour la meme raison : a l'egalite exacte, l'ecriture
            // de cette case est deja commencee.
            // BARRIERE COMPLETE, ET PAS SEULEMENT UNE LECTURE VOLATILE.
            //
            // Volatile.Read a une semantique d'acquisition : elle empeche ce qui suit de
            // remonter avant elle. Elle n'empeche <b>pas</b> ce qui precede de descendre
            // apres — or ce qui precede est justement la copie des cent douze octets. Sans
            // barriere pleine, rien n'interdit au compilateur de placer la copie apres la
            // relecture du curseur, ce qui viderait la verification de tout son sens.
            //
            // C'est le schema du seqlock : lire le compteur, copier, barriere, relire le
            // compteur, et rejeter s'il a bouge.
            Thread.MemoryBarrier();
            var after = Volatile.Read(ref *(long*)(_base + 64));
            if (after - _read >= _capacity)
            {
                Missed += after - _read - _capacity + 1;
                _read = after - _capacity + 1;
                continue;
            }

            _read++;
            Volatile.Write(ref *(long*)(_base + 128), _read);
            return true;
        }
    }

    public void Dispose()
    {
        _view.SafeMemoryMappedViewHandle.ReleasePointer();
        _view.Dispose();
        _file.Dispose();
    }
}
