#!/usr/bin/env python3
# Lecteur minimal d'export.pdb, la base que rekordbox ecrit sur une cle USB (format DeviceSQL).
#
# Ecrit d'apres la description publique du format (crate-digger, rekordbox_pdb.ksy) : des
# pages de 4 Ko, une table par type, des lignes adressees par des groupes de seize decalages
# en fin de page, des chaines a en-tete court ou long. On ne lit que ce qui sert ici : la
# table des pistes (type 0 : titre, chemin, tempo, tonalite) et celle des tonalites (type 5).
# Sur la cle du 25 septembre 2026 : 1 957 pistes, 24 tonalites, ecrites en Camelot par
# rekordbox lui-meme.
#
#   python3 outils/rekordbox_pdb.py /run/media/swave/BF2C-F971/PIONEER/rekordbox/export.pdb
import struct, sys
def chaine(b, pos):
    k = b[pos]
    if k & 1:
        n = ((k - 1) // 2) - 1
        return b[pos+1:pos+1+n].decode('ascii', 'replace')
    L, = struct.unpack_from('<H', b, pos+1)
    corps = b[pos+4:pos+L]
    return corps.decode('utf-16-le', 'replace') if k == 0x90 else corps.decode('ascii', 'replace')
def lignes(b, len_page, first, last):
    idx = first
    while True:
        p = idx * len_page
        gap, page_index, typ, nxt, _, _, nrs, _, _, flags, free, used, _, nrl, _, _ = struct.unpack_from('<IIIIIIBBBBHHHHHH', b, p)
        if flags & 0x40 == 0:
            n = nrl if (nrl > nrs and nrl != 0x1fff) else nrs
            for g in range((n + 15) // 16):
                base = p + len_page - g * 0x24
                present, = struct.unpack_from('<H', b, base - 4)
                for i in range(16):
                    r = g * 16 + i
                    if r >= n: break
                    if not (present >> i) & 1: continue
                    ofs, = struct.unpack_from('<H', b, base - 6 - 2*i)
                    yield p + 0x28 + ofs
        if idx == last or nxt == 0 or nxt >= len(b) // len_page: break
        idx = nxt
def lire(chemin):
    b = open(chemin, 'rb').read()
    _, len_page, num_tables = struct.unpack_from('<III', b, 0)
    tables = {}
    for i in range(num_tables):
        t, _, first, last = struct.unpack_from('<IIII', b, 28 + 16*i); tables[t] = (first, last)
    cles = {}
    for r in lignes(b, len_page, *tables[5]):
        kid, = struct.unpack_from('<I', b, r); cles[kid] = chaine(b, r + 8)
    # Les listes de lecture : l'arbre (type 7) et leurs entrees (type 8), dans l'ordre.
    listes = {}
    for r in lignes(b, len_page, *tables[7]):
        parent, _, ordre, lid, dossier = struct.unpack_from('<IIIII', b, r)
        listes[lid] = {'id': lid, 'nom': chaine(b, r + 20), 'dossier': bool(dossier), 'parent': parent, 'pistes': []}
    entrees = []
    for r in lignes(b, len_page, *tables[8]):
        rang, tid, lid = struct.unpack_from('<III', b, r)
        entrees.append((lid, rang, tid))
    for lid, rang, tid in sorted(entrees):
        if lid in listes: listes[lid]['pistes'].append(tid)
    pistes = []
    for r in lignes(b, len_page, *tables[0]):
        key_id, = struct.unpack_from('<I', b, r + 32)
        tempo, = struct.unpack_from('<I', b, r + 56)
        tid, = struct.unpack_from('<I', b, r + 72)
        ofs = struct.unpack_from('<21H', b, r + 0x5E)
        titre = chaine(b, r + ofs[17]); chemin_f = chaine(b, r + ofs[20])
        pistes.append({'id': tid, 'titre': titre, 'chemin': chemin_f, 'bpm': tempo / 100, 'cle': cles.get(key_id)})
    return pistes, cles, listes
if __name__ == '__main__':
    pistes, cles, listes = lire(sys.argv[1])
    for l in listes.values():
        if not l['dossier']: print(f"  liste « {l['nom']} » : {len(l['pistes'])} pistes")
    print(len(pistes), 'pistes,', len(cles), 'tonalites :', sorted(set(cles.values()))[:30])
    for p in pistes[:5]: print(p)
    import collections; print(collections.Counter(p['cle'] for p in pistes).most_common(8))
