# Le systeme : trois depots, et ce qui circule entre eux

emotion-calculator, le moteur de l'ensemble Emotion Emulator, n'est pas une application, c'est le maillon central d'une chaine qui va du
telephone au mur. Ce document fixe qui fait quoi et ce qui passe entre les trois.

```
   ┌─────────────┐        ┌──────────────────┐        ┌────────────────────┐
   │    crate    │        │emotion-calculator│        │  emotion-renderer  │
   │  telephone  │        │      .NET 10     │        │     CUDA / C++     │
   │             │        │                  │        │                    │
   │ bibliotheque│───────▶│     analyse      │───────▶│   deux processus   │──▶ mur
   │  du bac     │  fiche │   du signal      │ paquet │   cue │ master     │
   │             │        │                  │  128 o │                    │
   │  interface  │◀───────┼──────────────────┼────────│  etat, avancement  │
   └─────────────┘  notif └──────────────────┘        └────────────────────┘
        ▲                          ▲                            │
        │                          │                            │
     Le DJ                   table de mixage              retroprojecteur
                             master + cue
```

---

## Ce que chacun sait, et lui seul

| | `crate` | `emotion-calculator` | `emotion-renderer` |
|---|---|---|---|
| Ce qu'il detient | ce que le DJ **possede** et a **saisi** | ce qui **sonne**, maintenant | ce qui s'**affiche** |
| Famille, Camelot, pochette | source unique | jamais detectes | recus |
| Tempo, frappes, timbre | jamais stockes | mesures | recus |
| Formes, couleurs, mouvement | — | decrits | decides |
| Contrainte | aucune, c'est une bibliotheque | 43 ms d'analyse | l'image, 60 fois par seconde |

> **La regle qui gouverne les trois :** *le son donne le mouvement, la base donne le
> caractere.* Un tempo stocke est faux des la premiere seconde — un vinyle se joue a
> ±16 %. Une tonalite detectee pendant un fondu est fausse aussi — deux disques superposes
> produisent un accord qui n'existe dans aucun des deux.

---

## Les trois canaux

### 1. `crate` → `emotion-calculator` : la fiche du disque

Envoyee **au chargement**, une fois. Ce que la base sait et que le signal ne dira jamais.

    famille · Camelot · couleur · pochette · titre

Rien de rythmique, rien de spectral. Ces informations-la se mesurent.

### 2. `emotion-calculator` → `emotion-renderer` : le paquet, a chaque instant t

Le canal chaud. **128 octets**, ecrits dans un anneau partage sans verrou, mesures a
**1,5 µs** par message en `Release`.

Il part **a chaque fenetre d'analyse, sans condition** — environ 47 fois par seconde. Un
flux qui ne parlerait qu'en cas de changement priverait le renderer de son horloge, et
c'est l'horloge qui lui permet d'anticiper plutot que de reagir.

Ce qu'il transporte, par nature :

| Nature | Contenu |
|---|---|
| Instant | niveau, 12 bandes, frappes, notes graves/medium/aigues |
| Couleur | centroide, ouverture du filtre, densite |
| Temps | tempo, phase, rang du temps dans la mesure, position dans la phrase |
| Structure | tension, rupture, longueur de phrase, mesures avant la frontiere |
| Etats | filtre ferme, basse coupee, passage dense — a hysteresis |
| Fiabilite | `Trust`, et la confiance de chaque estimateur |

**`Trust` est la piece maitresse de ce canal.** Elle dit au renderer ce dont il a le droit
de se servir, et elle monte par les paliers du metier : 2 temps, 4 temps, 16 temps. Le
flux ne s'interrompt jamais — c'est un seuil de rendu, pas d'ecoute.

### 3. `emotion-renderer` → `crate` : l'etat et le feu vert

Le canal de retour, rare et asynchrone. Le GPU dit ou il en est.

    « j'ai assez pour faire, je connais les informations de la piste
      et j'ai charge ce que je sais du signal »

Cette notification arrive sur le telephone. C'est elle qui autorise le DJ a lancer la
bascule, et elle repond a une question qu'aucun des deux autres ne peut trancher : *le GPU
est-il pret ?*

Le seuil qui la declenche : **convergence des estimateurs, plafonnee a 30 secondes.**
`KnowledgeGate` la mesure, `SignalWorker` l'emet une fois, `/ready` la rend interrogeable
a tout moment — par exemple pour afficher une jauge en rouvrant l'application.

    GET /ready
    { "pret": true, "motif": "convergence", "avancement": 1, "secondesRestantes": 0,
      "tempo": 96.3, "confianceTempsFort": 0.7, "confianceStructure": 0, "fiabilite": 1 }

Le motif compte autant que le drapeau : `convergence` dit qu'on sait, `plafond` dit qu'on
annonce ce qu'on a. Dans le second cas, les confiances jointes disent a quoi se fier.

---

## Le GPU, deux processus

Le partage suit la musique et non la technique.

| | processus **cue** | processus **master** |
|---|---|---|
| Matiere | ce qui se repete : grille, frappes, basse, couleur | ce qui n'arrive qu'une fois : un solo, une voix |
| Quand | pendant le calage, hors du temps de la salle | a l'instant meme |
| Contrainte | aucune | tout |
| Cycle | `read` → `load` → `execute` | direct |

Le percussif et le structurel se repetent, donc se precalculent. Le melodique et
l'expressif sont uniques, donc restent en direct. **L'equilibrage de charge n'est pas une
optimisation, c'est la forme de la musique.**

Le parallelisme interne — flux CUDA, files, synchronisation — appartient au renderer. Ce
depot ne s'en occupe pas et ne doit pas le supposer.

---

## Ce que le mur recoit

Le rétroprojecteur est la sortie du processus master, qui compose :

- ce que le cue avait prepare, pour ce qui se repete,
- ce que le master entend a l'instant, pour ce qui ne se repete pas,
- pondere par `Trust`, qui dit ce qui merite d'etre cru.

---

## Etat des trois depots

| | Depot | Etat |
|---|---|---|
| `crate` | existant, PWA en ligne | la bibliotheque tourne ; la fiche vers l'analyse reste a cabler |
| `emotion-calculator` | ce depot | analyse complete, 90 tests, transport mesure a 1,5 µs |
| `emotion-renderer` | **a creer** | CUDA / C++ |

La fiche arrive par `POST /deck/cue`, `/deck/take` et `/deck/play`, et porte titre, disque,
face, Camelot, famille, couleur et pochette. Deux choses en decoulent, qui manquaient :

- **le Camelot pilote enfin la geometrie.** Il traversait tout le systeme sans rien
  commander — recu, transporte, documente dans trois fichiers, et lu nulle part. La regle
  vivait en double, dans le renderer et nulle part ailleurs, et les deux versions avaient
  deja diverge. Elle appartient desormais a la fiche, qui la publie ; le rendu la lit ;
  le paquet la transporte.
- **poser un disque previent l'analyse.** `IAudioSource.NewTrack()` remet le seuil de
  connaissance a zero. Sans cela, les estimateurs — dont l'oubli est lent par
  construction — mettraient des dizaines de secondes a admettre le changement, en
  annoncant pendant tout ce temps qu'ils « connaissent » un morceau qui ne joue plus.

### Ce qui manque cote emulator

- Le **canal de retour** depuis le renderer

Le reste — analyse, structure, gestes, transport, seuil de connaissance et feu vert — est
en place et mesure.
