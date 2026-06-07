# ArmA File Format Library (Amélioré)

Cette version modifiée de la librairie apporte plusieurs optimisations et nouvelles fonctionnalités par rapport au projet d'origine.

## Améliorations apportées

- **Amélioration des performances et de la mémoire** :
  - Réduction drastique de l'utilisation mémoire pour les fichiers WRP.
  - Optimisation et correctifs de l'encodeur PAA.
  - Migration vers `ImageSharp` (mise à jour majeure) pour un traitement plus rapide et moderne des images.
  - Correction de l'implémentation de la compression LZSS (avec l'aide de Roman Vostrikov).

- **Nouvelles fonctionnalités et supports de formats** :
  - Ajout du support pour les modèles **ODOL v75**.
  - Ajout du support pour le format compilé **Sqfc**.
  - Capacité à lire les coordonnées UV.
  - Ajout de méthodes utilitaires ("convenience methods") facilitant grandement la manipulation des fichiers de configuration.
  - Améliorations de l'utilitaire `WrpUtil`.
