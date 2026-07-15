# Game Realistic Map — Boosted Edition — Guide utilisateur

Guide complet de la Boosted Edition. Pour la liste rapide des fonctionnalités voir le [README](../README.md) ; pour le fonctionnement interne du mode île et le réglage des performances voir [boosted-edition.fr.md](boosted-edition.fr.md). *(English: [user-guide.md](user-guide.md).)*

## Sommaire

1. [Installation & prérequis](#installation--prérequis)
2. [Générer une carte depuis des données réelles](#générer-une-carte-depuis-des-données-réelles)
3. [Mode île](#mode-île)
4. [Élévation haute résolution Swisstopo](#élévation-haute-résolution-swisstopo)
5. [Importer une carte existante (jeu ou mods)](#importer-une-carte-existante-jeu-ou-mods)
6. [L'éditeur de carte (World Editor)](#léditeur-de-carte-world-editor)
   - [Imagerie : satmap & masque](#imagerie--satmap--masque)
   - [Régénérer le satmap depuis les surfaces](#régénérer-le-satmap-depuis-les-surfaces)
   - [Grille d'élévation & export heightmap Minecraft](#grille-délévation--export-heightmap-minecraft)
   - [Objets : import, remplacer, réduire, supprimer](#objets--import-remplacer-réduire-supprimer)
7. [Générer le mod (packaging)](#générer-le-mod-packaging)
8. [Réglage des performances](#réglage-des-performances)
9. [Réglages recommandés](#réglages-recommandés)

---

## Installation & prérequis

- Télécharge le dernier build depuis les [Releases](https://github.com/Valou130901/ArmaRealMap_boosted_edition/releases).
- Dézippe et lance `GameRealisticMap.Studio.exe`.
- Nécessite le **.NET 8 Desktop Runtime**.
- Pour packager un mod Arma 3 jouable, tu as aussi besoin des **Arma 3 Tools** (Steam) : ils montent le project drive (`P:`) et fournissent Binarize. Le compilateur PBO intégré supprime la dépendance aux outils Mikero.

---

## Générer une carte depuis des données réelles

1. **Accueil → nouvelle config de carte** (`.grma3m`).
2. Renseigne les **coordonnées du centre** (ou dessine une zone sur la carte OSM avec Ctrl+glisser / Alt+glisser).
3. Définis la **taille** et la **grille**. L'éditeur affiche `grille × taille de cellule → total mètres`. Vise une **taille de cellule de 2 à 2,5 m**.
4. Choisis un **style de carte** (`builtin:CentralEurope.grma3a` pour la campagne européenne). Clique **Modifier** pour changer la bibliothèque d'assets (bâtiments, végétation…).
5. Active éventuellement le **Mode île** et **Swisstopo** (voir plus bas).
6. **Générer un aperçu** pour un rendu rapide, **Générer un fichier carte pour Arma 3** pour le WRP, ou **Générer un mod pour Arma 3** pour le mod jouable complet.

Tout le terrain, les routes, les empreintes de bâtiments, les forêts et les champs viennent d'OpenStreetMap ; l'élévation de SRTM / AW3D30 (ou Swisstopo en Suisse) ; les couleurs satellite de Sentinel-2.

---

## Mode île

Transforme n'importe quelle frontière administrative OSM (district, commune, canton…) en une île entourée d'océan.

**Utilisation :**
1. Coche **Island Mode (experimental)**.
2. Clique **Search…**, choisis la frontière OSM (ex. *« District de la Glâne, Fribourg »*). Le centre et la taille de la carte sont réglés automatiquement pour contenir la frontière (+20 % de marge).
3. Génère normalement.

**Ce qu'il fait (entièrement automatique) :**
- **Aucune déformation du terrain à l'intérieur de la frontière.** Toute la carte est translatée verticalement pour que le point le plus bas du district soit juste au-dessus du niveau de la mer ; le relief réel est préservé au 1:1.
- **Côtes naturelles hors de la frontière.** Le terrain descend depuis l'altitude du bord de frontière jusqu'au fond marin (-50 m, atteint à ~500 m) selon une courbe smoothstep. Le champ d'altitude du bord est lissé pour qu'une frontière vallonnée ne crée plus de murs verticaux ni de coutures. Les bords bas deviennent des plages, les bords hauts des pentes/falaises progressives.
- **Anti-inondation.** Le terrain dans la frontière reste au-dessus de 0 (0,2 m minimum), appliqué de nouveau *après* le solveur routes/rivières pour que les embouchures et routes côtières ne creusent jamais sous l'océan.
- **Pas de trous.** Toute zone « océan » enfermée dans l'île (artefact de rasterisation ou de géométrie) est retransformée en terre.
- **Rendu du fond marin.** Hors frontière, la texture du sol est forcée en sable (plage/fond propre, sans algues) et l'image satellite est teintée en eau selon la profondeur (turquoise tropical près de la côte, lagon profond au large).

Si tu veux de vraies plages là où la frontière est perchée haut, tu les sculptes toi-même ensuite — la côte automatique est un tampon qui ne touche jamais le terrain réel intérieur.

---

## Élévation haute résolution Swisstopo

Pour les cartes suisses, coche **Swisstopo high-res elevation**. Le moteur télécharge la topographie haute résolution swissALTI3D au lieu du DEM global ~30 m.

- C'est de là que vient le vrai détail du terrain — falaises, berges, talus.
- Le premier run télécharge un gros volume de données (mis en cache ensuite).
- Fonctionne uniquement pour les cartes situées en Suisse.

---

## Importer une carte existante (jeu ou mods)

**Menu Fichier → « Import a map from game or mods… »**

1. L'outil scanne tous les PBO d'Arma 3, les mods actifs et tout le contenu Workshop. Les PBO protégés/obfusqués injectent des entrées leurres ; elles sont filtrées (seules les vraies cartes avec un nom valide et une taille non nulle sont listées).
2. Chaque ligne a un bouton **Import**. Optionnel :
   - **Custom PBO prefix** (ex. `moi\malden_custom`) — crée une version indépendante pour que ton mod n'écrase pas la carte d'origine.
   - **Custom world name** — le nom technique de ta version.
   - Laisse les deux vides pour garder le préfixe d'origine.
3. À l'import, l'outil :
   - Extrait tout le PBO de la carte (wrp, config, shapefiles de routes, données).
   - Convertit le `.wrp` binarisé (OPRW) en format éditable.
   - Extrait les couches d'imagerie où qu'elles soient (les chemins de matériaux du wrp servent à les localiser, même dans un PBO séparé ou un autre mod), décode les tuiles `.paa` (masque/satmap) en PNG, et reconvertit les rvmat binarisés en texte.
   - Ouvre la carte dans le World Editor avec imagerie, élévation, objets et matériaux éditables.

**Limites connues :**
- Le réseau routier binarisé des cartes officielles n'est pas éditable.
- Binarize peut planter sur certaines cartes officielles une fois dé-binarisées (bug d'un outil BI). Si le packaging échoue, édite le terrain/objets puis régénère une carte GRM fraîche, ou utilise la carte seulement pour de l'édition perso.
- Publier un mod contenant un wrp de carte BI copié est une zone grise légale — usage perso uniquement.
- L'import/déobfuscation de PBO **protégés** n'est volontairement pas supporté.

---

## L'éditeur de carte (World Editor)

Ouvre un `.wrp` (double-clic, fichiers récents, ou après un import). L'éditeur a des sections : Imagerie, Matériaux du sol, Grille de dénivelé, Objets, Dépendances, plus l'**éditeur de carte visuel** (Ouvrir l'éditeur de carte).

### Imagerie : satmap & masque

- **Exporter l'image satellite / le masque de texture** — exporte le satmap ou le masque assemblé en PNG.
- **Importer l'image satellite / le masque de texture** — réimporte un PNG édité (met à jour les tuiles).
- Taille de tuile, taille totale et résolution sont affichées. Le chevauchement des tuiles est mesuré depuis les fichiers de la carte, donc les cartes importées avec une grille non-GRM fonctionnent.

### Régénérer le satmap depuis les surfaces

**Generate SatMap from IdMap** reconstruit l'image satellite depuis les surfaces peintes, avec le rendu doux d'un satmap généré par GRM :
- Chaque surface est remplie avec la tuile basse résolution de sa **vraie texture de sol du jeu** (herbe, asphalte, sable…), puis toute l'image est floutée (gaussien) pour un fondu naturel entre surfaces.
- Sers-t'en après avoir édité le masque de texture (Surface Painter, ou peinture du masque) pour que la photo satellite colle à tes nouvelles routes / eau / canaux.
- Workflow : exporte le masque vers `<prefix>\IdMap.png` → **Generate SatMap from IdMap** → il écrit `…-satmap_corrected.png` → **Importer l'image satellite** pour l'appliquer.
- Note : pour quelques retouches localisées, éditer le satmap existant directement dans un logiciel d'image préserve mieux le détail de la vraie photo.

### Grille d'élévation & export heightmap Minecraft

- **Importer / Exporter (Esri ASCII .asc)** — aller-retour de la grille d'élévation.
- **Export heightmap PNG (Minecraft)** — exporte un heightmap 16 bits en niveaux de gris + un `…readme.txt` documentant le mapping des valeurs (altitude min/max, valeur de gris du niveau de la mer, taille de pixel). Importe-le dans [WorldPainter](https://www.worldpainter.net/) pour un monde Minecraft :
  - Échelle 1 pixel = 1 bloc pour un monde 1:1 (recommandé ; garde les proportions réelles et l'échelle verticale dans les -64…320 de Minecraft).
  - Règle le niveau de l'eau sur la valeur de gris donnée dans le readme.

### Objets : import, remplacer, réduire, supprimer

- **Importer depuis un fichier** — importe des objets (export Terrain Builder / Eden).
- **Exporter vers un fichier** — exporte les objets + une bibliothèque `.tml`.
- **Remplacer** — remplacement en masse d'un modèle par un autre.
- **Réduire** — éclaircissement en masse des objets. **Réduction par type :** coche *« Par type (motif) »* pour que le champ Modèle devienne un motif (substring), ou utilise les boutons rapides **Arbres / Buissons / Herbe·clutter / Rochers**. Une seule règle réduit *tous* les modèles d'arbres d'un coup (ex. facteur 0,5 enlève la moitié). Les compteurs initial et restant estimé se mettent à jour en direct.
- **Supprimer tous les objets** — retire tous les objets pour repartir de zéro (avec confirmation). Sauvegarde le wrp pour persister.
- **Prendre les images aériennes** — captures aériennes.

---

## Générer le mod (packaging)

**Générer un mod pour Arma 3** construit le `@mod` jouable.

Deux moteurs de packaging (Outils → Options → Arma 3) :
- **Outil intégré** (par défaut, décoche *Use PboProject*) — optimisé dans ce fork : préparation des modèles parallèle, copies de modèles réutilisées entre runs, lecture d'en-tête seule, immunisé au crash MakePbo lac-en-bord.
- **PboProject** (Mikero) — l'outil externe classique.

Binarize lui-même (le compilateur de terrain BI) est mono-thread et non parallélisable ; c'est souvent l'étape la plus longue.

---

## Réglage des performances

- **Exclusions Windows Defender** — le plus gros gain gratuit. Binarize/packaging lisent des milliers de petits fichiers. Exclus `P:\`, ton dossier de mods et le dossier Arma 3 Tools (PowerShell admin) :
  ```powershell
  Add-MpPreference -ExclusionPath 'P:\'
  Add-MpPreference -ExclusionPath "$env:USERPROFILE\Documents\GameRealisticMap"
  Add-MpPreference -ExclusionPath 'C:\Program Files (x86)\Steam\steamapps\common\Arma 3 Tools'
  ```
  Effet immédiat, sans redémarrage.
- **Garde `P:\` et les caches sur SSD/NVMe.**
- **Les re-runs sont bien plus rapides** — tuiles satellite, élévation Swisstopo et modèles préparés sont mis en cache.
- **Tous les cœurs CPU sont utilisés** — placement d'objets, traitement d'images, teinte satellite, traitement île et préparation PBO tournent tous en parallèle (pas de bride de threads).
- **Itère sans binarize** — utilise *Générer un fichier carte* (WRP seul) pour tester le terrain, ne build le mod complet que pour les versions finales.

---

## Réglages recommandés

| Réglage | Recommandation | Pourquoi |
|---|---|---|
| Taille de cellule | **2 à 2,5 m** | En dessous de 2 m : 4× le travail, quasi aucun gain visuel. Le détail vient des données source, pas de la densité de grille. |
| Taille de grille | Taille carte ÷ taille cellule | ex. 8192 m → grille 4096 × 2 m ; 20480 m → grille 8192 × 2,5 m |
| Swisstopo | **Activé** pour les cartes suisses | D'où vient le détail du terrain. |
| Texture mask multiplier | 2 | Transitions de surfaces plus fines, coût modéré. |
| Résolution satellite | 1 m/pixel | Seule valeur testée. |
| Outil PBO | **Intégré** | Optimisé, sans dépendance Mikero. |
