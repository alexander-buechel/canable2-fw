# AGENTS.md

## Dokumentation

- `docs/rm-stm32g4/` enthält das Reference Manual des STM32G4 (RM0440) in Markdown-Form, aufgeteilt in eine Datei pro Kapitel (z. B. `FD_controller_area_network_(FDCAN).md`, `General-purpose_I__47_Os_(GPIO).md`).
  Bei Fragen zu Peripherie, Registern oder Taktung zuerst dort nachschlagen. Das PDF von ST bleibt die offizielle Referenz; die Markdown-Version kann in Formatierung, Tabellen und Bildern abweichen (siehe `docs/rm-stm32g4/00_README.md`).
- `docs/agent/` enthält weitere Notizen für Agents, z. B. `renode-usb-cdc-platform.md`.

## Hardware

- MCU: STM32G4 im 48-Pin-Gehäuse (kein Port D verfügbar, also kein PD12/PD13).
- PB8 soll nicht für CAN verwendet werden (BOOT0-Pin).
- PA11/PA12 sind USB (D−/D+) und damit für CAN belegt.
- Die Pin-/AF-Tabelle steht im Datasheet, nicht im Reference Manual unter `docs/rm-stm32g4/`.
