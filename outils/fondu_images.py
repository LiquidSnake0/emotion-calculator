#!/usr/bin/env python3
"""Des images de la fenetre a des instants choisis d'un fondu enregistre, sans ecran.

    python3 outils/fondu_images.py ~/.cache/emotion-emulator/relais/A--B.pak dossier/

Le fondu enregistre par `outils/relais.py` est rejoue paquet par paquet a sa cadence dans
une fenetre hors ecran, et huit images sont ecrites : A seul, l'instant de l'accueil et
0,2 s puis 1,3 s apres, la mi-fondu, l'instant du retrait et 0,25 s apres, B seul. C'est la
mesure a l'oeil de la composition par platine : la scene qui entre doit s'ouvrir sans saut,
celle qui sort se fermer sans que rien ne change de cote.
"""
import os
import struct
import sys
import traceback

os.environ.setdefault("QT_QPA_PLATFORM", "offscreen")
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import fenetre  # noqa: E402
from PySide6.QtWidgets import QApplication  # noqa: E402
from PySide6.QtGui import QImage  # noqa: E402


class Enregistre:
    """Un anneau qui rend toujours le meme paquet : celui qu'on lui designe."""

    def __init__(self, octets):
        self.octets = octets
        self.i = 0

    def dernier(self):
        return fenetre.Paquet(self.octets, self.i * 256)


def main():
    if len(sys.argv) != 3:
        print(__doc__)
        return 1
    octets = open(sys.argv[1], "rb").read()
    dossier = sys.argv[2]
    os.makedirs(dossier, exist_ok=True)
    n = len(octets) // 256
    fondu = [i for i in range(n) if octets[i * 256 + fenetre.P_RELAIS] & 4]
    if not fondu:
        print("aucun fondu dans ce fichier")
        return 1
    debut, fin = fondu[0], fondu[-1] + 1

    # Le temps est celui des paquets, pas celui de la machine : chaque image d'analyse
    # vaut 21,3 ms, et la fenetre lit son horloge la.
    horloge = [0.0]
    fenetre.time.monotonic = lambda: horloge[0]
    leve = []
    vrai = fenetre.Mur.paintEvent

    def garde(self, e):
        try:
            vrai(self, e)
        except Exception:
            leve.append(traceback.format_exc())
    fenetre.Mur.paintEvent = garde

    app = QApplication([])  # noqa: F841
    m = fenetre.Mur()
    m.anneau = Enregistre(octets)
    m.derniere_sequence = -1
    m.resize(1180, 780)
    img = QImage(1180, 780, QImage.Format.Format_RGB32)

    def avancer(a, b):
        for i in range(a, b):
            m.anneau.i = i
            horloge[0] += 0.0213
            m.battre()

    def rendre(nom):
        m.render(img)
        img.save(os.path.join(dossier, nom + ".png"))
        p = m.paquet
        print(f"{nom:20s} t={p.temps / 1000:6.1f} s  ouverture P1 {m.ouverture[1]:.2f} P2 {m.ouverture[2]:.2f}"
              f"  fader vu P1 {m.somme_vue[1]:.2f} P2 {m.somme_vue[2]:.2f}"
              f"  platines {[p.sources[r]['platine'] for r in range(p.actives)]}")

    etapes = [(max(0, debut - 100), debut - 1, "1-A-seul"), (debut - 1, debut + 1, "2-accueil"),
              (debut + 1, debut + 10, "3-accueil+0.2s"), (debut + 10, debut + 60, "4-accueil+1.3s"),
              (debut + 60, (debut + fin) // 2, "5-mi-fondu"), ((debut + fin) // 2, fin + 1, "6-retrait"),
              (fin + 1, fin + 12, "7-retrait+0.25s"), (fin + 12, min(n, fin + 120), "8-B-seul")]
    for a, b, nom in etapes:
        avancer(a, b)
        rendre(nom)
    if leve:
        print(leve[0])
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
