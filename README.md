# Google Drive Connector for Autodesk Revit

[![Autodesk Revit](https://img.shields.io/badge/Revit-2025%20%7C%202026%20%7C%202027-blue.svg)](https://www.autodesk.com/products/revit/overview)
[![Platform](https://img.shields.io/badge/Platform-Windows%2010%20%2F%2011%20x64-0078D6.svg)](https://www.microsoft.com/windows)
[![Framework](https://img.shields.io/badge/.NET-10.0%20WPF-512BD4.svg)](https://dotnet.microsoft.com/)
[![License: MIT](https://img.shields.io/badge/License-MIT-green.svg)](LICENSE)
[![Google Drive](https://img.shields.io/badge/Cloud-Google%20Drive%20v3-4285F4.svg)](https://www.google.com/drive/)

Google Drive Connector e una soluzione completa di Worksharing e Collaborazione Cloud per Autodesk Revit (versioni 2025, 2026 e 2027) basata su Google Drive.

Permette a piu architetti e ingegneri di lavorare contemporaneamente sullo stesso Modello Centrale BIM da postazioni remote, prevenendo corruzioni dei file e senza richiedere le costose licenze di Autodesk BIM Collaborate Pro.

---

## Download Immediato
Scarica la versione piu recente direttamente dalla sezione [Releases](https://github.com/FilippoAldrigo/revit-drive-connector/releases):

* [Scarica RevitDriveConnector_Setup.exe](https://github.com/FilippoAldrigo/revit-drive-connector/releases/latest/download/RevitDriveConnector_Setup.exe) (Installer per Windows)
* [Scarica RevitDriveConnector_Installatore.zip](https://github.com/FilippoAldrigo/revit-drive-connector/releases/latest/download/RevitDriveConnector_Installatore.zip) (Archivio portabile)
* [Scarica Codice Sorgente .ZIP](https://github.com/FilippoAldrigo/revit-drive-connector/releases/latest/download/RevitDriveConnector_SourceCode.zip) (Progetto completo Visual Studio / C#)

---

## Funzionalita Principali

### 1. Prevenzione Corruzioni e Lock Esclusivo (Gatekeeper Anti-Desync)
Nei normali cloud storage, sincronizzazioni simultanee corrompono il file centrale (.rvt). 
Il plugin introduce un motore di mutua esclusione:
* Prima di qualsiasi sincronizzazione (STC), acquisisce un Lock Esclusivo distribuito.
* Il Gatekeeper verifica l'allineamento degli hash SHA-256 e attende che i delta cloud siano completati sul disco prima di consentire a Revit di salvare.
* Nessun conflitto di versione o sovrascrittura accidentale.

### 2. Cache Locale su SSD e Prestazioni Elevate
Revit non lavora mai direttamente sui percorsi cloud lenti. I modelli locali risiedono in una cache SSD veloce, garantendo apertura, salvataggio e navigazione 3D fulminee.

### 3. Gestione Collaboratori Diretta via Email
Dall'interno di Revit puoi invitare qualsiasi collega inserendo semplicemente il suo indirizzo email Google:
* Il plugin assegna i permessi della cartella tramite le API Google Drive v3.
* Il collega apre Revit e trova automaticamente il progetto tra i "Condivisi con me" in "Apri da Google Drive".

### 4. Centro Prestiti in Tempo Reale (Worksharing Monitor Cloud)
Possibilita di richiedere e concedere elementi BIM con notifiche popup Live Toast istantanee al proprietario, senza dover sincronizzare l'intero file.

### 5. Auto-Healing dei Collegamenti Esterni (CAD, IFC, RVT)
Consolida automaticamente i link esterni nella sottocartella Links con percorsi relativi conformi, eliminando l'errore "Collegamento non trovato".

### 6. Interfaccia Indistinguibile da Autodesk Revit
* Scheda Ribbon nativa "Drive Connector" con 10 icone vettoriali dedicate (32x32 e 16x16) con trasparenza nativa compatibile con tema chiaro e scuro.
* Ancoraggio automatico dei file aperti nella Home Page di Revit (File Recenti).

---

## Comandi della Barra Multifunzione (Ribbon)

| Comando | Descrizione |
| :--- | :--- |
| Apri da Google Drive | Esplora i modelli centrali condivisi nel cloud e crea automaticamente la copia locale. |
| Pubblica su Drive | Attiva il worksharing sul progetto corrente e lo carica come modello centrale condiviso. |
| Condividi Progetto | Aggiunge collaboratori al modello tramite la loro email Google. |
| Sincronizza Drive STC | Esegue la sincronizzazione protetta con lock esclusivo e allineamento delta. |
| Stato e Utenti Attivi | Monitora la connessione cloud, lock correnti e collaboratori connessi. |
| Chiedi Modifica Elemento | Invia una richiesta di prestito istantanea per l'oggetto selezionato. |
| Centro Prestiti | Visualizza, approva o rifiuta le richieste di prestito ricevute dal team. |
| Gestione Collegamenti | Relativizza e consolida i collegamenti CAD, IFC e RVT nella cartella cloud. |
| Account Google | Gestisce la connessione OAuth 2.0 all'account Google Drive. |
| Impostazioni Cloud | Configura i percorsi dello storage cloud e della cache veloce locale. |

---

## Guida Rapida all'Installazione

1. Scarica ed esegui RevitDriveConnector_Setup.exe.
2. L'installer rileva automaticamente le versioni di Autodesk Revit presenti (2025, 2026, 2027) e installa i componenti.
3. Apri Revit, accedi al tuo account Google dalla scheda Drive Connector -> Account Google.
4. Sei pronto a collaborare.

---

## Architettura Tecnica

Il sistema e strutturato in 5 componenti modulari in .NET 10 (C#):
* RevitDriveConnector.Addin: Plugin Revit nativo compilato contro le API Autodesk Revit 2027 / AdWindows.
* RevitDriveConnector.Core: Libreria condivisa (IPC, lock manager distribuito, hash SHA-256, protocolli di sincronizzazione).
* RevitDriveConnector.Daemon: Servizio di background locale Windows per la gestione dei lock, Named Pipes e Google Drive API v3.
* RevitDriveConnector.Server: Coordinator server opzionale per LAN discovery ad altissima velocita.
* RevitDriveConnector.Installer: Setup standalone WPF con grafica Autodesk Human Interface Guidelines.

---

## Privacy e Licenza

* Privacy Policy: Consulta il file PRIVACY.md per i dettagli sulla gestione locale e sicura dei token OAuth.
* Licenza: Rilasciato sotto licenza libera MIT License.
* Disclaimer: Google Drive Connector for Autodesk Revit e un progetto open-source indipendente e non e affiliato, sponsorizzato ne approvato da Autodesk, Inc. ne da Google LLC. Autodesk e Revit sono marchi registrati di Autodesk, Inc. Google e Google Drive sono marchi registrati di Google LLC.
