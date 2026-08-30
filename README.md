# Game Realistic Map (Boosted Edition)

*(English: [README.en.md](README.en.md).)*

![](./GameRealisticMap.Studio/Resources/Icons/grms128.png)

Ce fork est une version fortement remaniée de Game Realistic Map, taillée pour la performance, les données haute résolution et des fonctions de génération de terrain avancées.

**Téléchargement : voir les [Releases](https://github.com/Valou130901/ArmaRealMap_boosted_edition/releases) — dézippe et lance `GameRealisticMap.Studio.exe` (runtime .NET 8 Desktop requis).**

📖 **[Guide utilisateur complet](docs/user-guide.fr.md)** — toutes les fonctions pas à pas. Détails du mode île et réglages de performance : [docs/boosted-edition.fr.md](docs/boosted-edition.fr.md). Export BeamNG.drive : [docs/beamng.fr.md](docs/beamng.fr.md).

🇬🇧 **[Complete user guide (English)](docs/user-guide.md)** — the [Boosted Edition guide](docs/boosted-edition.md) and the [BeamNG.drive export](docs/beamng.md).

## 🚀 Ce que la Boosted Edition ajoute

### ⚡ Performance maximale (100 % du CPU)
Toutes les limites de threads arbitraires ont sauté : génération des objets, conversion d'images, remplissage géométrique, teinte satellite de l'eau, altimétrie du mode île et préparation des modèles PBO tournent en parallèle sur tous les cœurs logiques.

### 🏔️ Altimétrie Swisstopo swissALTI3D
Intégration automatique des données d'élévation haute résolution **Swisstopo swissALTI3D**. Sur une carte suisse, le moteur travaille sur une topographie ultra-précise.

### 🚗 Export BeamNG.drive
Construis un niveau BeamNG jouable, soit **directement depuis les données réelles** (éditeur de config → *Générer un niveau BeamNG.drive*), soit **depuis un monde Arma** que tu possèdes déjà. Sur une carte suisse, l'interrupteur Swisstopo qui donne l'altimétrie apporte aussi les vrais bâtiments, les vrais arbres et la vraie photo aérienne :
* **Volumes swissBUILDINGS3D** avec leurs vraies toitures. La donnée ne contient que de la géométrie : murs et toits sont donc distingués par l'inclinaison de chaque face et dépliés en mètres, une tuile de façade couvrant un étage — une fenêtre fait la même taille sur tous les bâtiments.
* **Arbres réels par la canopée** : swissSURFACE3D moins le terrain, chaque sommet local est un arbre qui existe vraiment, à la hauteur qu'il a vraiment. L'espèce vient de cette hauteur ; les couronnes posées sur un toit ou au-dessus d'une chaussée sont supprimées.
* **Sol SWISSIMAGE** au lieu de Sentinel-2, et maillages Arma issus de la bibliothèque partagée pour les arbres, les roches et les tabliers de pont.
* `tools/check-beamng-export.py` lit un zip exporté et rend un rapport sur les normales, les textures, le profil des routes, les carrefours, les ponts, l'altitude de la forêt et les points de spawn — chaque contrôle existe parce qu'un vrai défaut est passé devant.

Voir [docs/beamng.fr.md](docs/beamng.fr.md).

### 🏝️ Mode île avancé
Transforme n'importe quelle limite administrative OSM en île avec des côtes naturelles :
* **Profil de côte continu** : le terrain descend du relief réel jusqu'au profil des fonds — plages douces sur les côtes basses, falaises progressives sur les hautes, la rampe s'adaptant à l'altitude du bord et plafonnée à ~12 % de pente. Le fond (-50 m) est atteint à ~500 m de la limite. Plus de tranchée ni de mur le long de la côte.
* **Sécurité anti-noyade** : la terre à l'intérieur de la limite reste au-dessus du niveau de la mer (0,2 m minimum), garanti à nouveau après le solveur de contraintes routes/rivières, pour qu'une embouchure ou une route côtière ne puisse pas creuser sous l'océan.
* **Fond marin correct** : hors de la limite, la texture du sol est forcée en fond océanique — l'occupation du sol OSM ne déborde plus dessus — et l'image satellite est teintée selon la profondeur.
* **Rapide quelle que soit la grille** : le polygone est rastérisé une fois au lieu de millions de tests point-dans-polygone, la passe d'altimétrie insulaire prend quelques secondes même en 8192×8192.

### 📦 Compilateur PBO intégré rapide
L'outil d'empaquetage intégré (Options → Arma 3 → décocher *Use PboProject*) a été largement optimisé :
* Préparation des modèles parallèle, avec réutilisation des copies en cache entre deux passages (quasi instantané en reprise).
* Détection de classe par lecture du seul en-tête P3D, au lieu de parser l'ODOL entier.
* Aucune dépendance aux outils de Mikero, et immunité au plantage MakePbo sur un lac en bord de carte.

### 🗺️ SatMap et IdMap
* **Reconstruction de la SatMap** : régénère une image satellite corrigée (`satmap_corrected.png`) depuis ton `IdMap` retouché. Chaque surface est remplie avec sa **vraie texture de sol en jeu** (herbe, asphalte, sable…) puis floutée en gaussienne, pour retrouver le rendu doux d'une satmap générée nativement — parfait après avoir peint des routes ou de l'eau.
* **Recherche Nominatim améliorée** : noms de limites complets au lieu d'identifiants bruts. Choisir une limite d'île centre la carte et l'ajuste automatiquement (+20 % de marge).

### 📥 Importer une carte existante (jeu et mods)
Importe n'importe quelle carte Arma 3, officielle ou issue d'un mod, pour l'éditer : **Fichier → Importer une carte depuis le jeu ou les mods**.
* Balaie le jeu, les mods actifs et tout le Workshop ; **les entrées parasites des PBO protégés ou obfusqués sont écartées**.
* Extrait le wrp (OPRW binarisé pris en charge), la config, les routes et **les couches d'imagerie où qu'elles soient** — les tuiles `.paa` sont décodées en PNG, les rvmat binarisés reconvertis en texte, donc satmap et id map redeviennent éditables.
* **Préfixe PBO et nom de monde personnalisés** en option, pour construire ta propre version indépendante au lieu d'écraser la carte d'origine.

### 🌲 Réduire les objets par type
L'outil **Réduire** peut éclaircir toute une catégorie d'un coup — coche *« Par type (motif) »* ou utilise les boutons **Arbres / Buissons / Herbe / Rochers**. Une seule règle traite tous les modèles correspondants (retirer la moitié des arbres, par exemple) au lieu d'un modèle à la fois. Un bouton **« Supprimer tous les objets »** repart de zéro.

### ⛏️ Export Minecraft / WorldPainter
Exporte la grille d'élévation en **PNG niveaux de gris 16 bits** (avec un readme documentant altitudes, niveau de la mer et échelle), prêt à importer dans WorldPainter pour un monde Minecraft au 1:1.

### 📦 Dépendances moteur mises à niveau
Bibliothèque `bis-file-formats` remise à jour :
* Consommation mémoire des WRP nettement réduite.
* Prise en charge des modèles **ODOL v75** et des formats compilés **Sqfc** d'Arma 3.
* Corrections de l'encodeur PAA et passage à ImageSharp pour un traitement de textures robuste.

---

*(Toutes les fonctions d'origine de Game Realistic Map restent disponibles.)*

## Sources de données utilisées
  - NASA SRTM (automatique)
  - JAXA AW3D30 (automatique)
  - OpenStreetMap (automatique)
  - Sentinel-2 cloudless (automatique)
  - **Swisstopo swissALTI3D (exclusif à ce fork)**
  - **Swisstopo swissSURFACE3D, swissBUILDINGS3D et SWISSIMAGE (export BeamNG)**
