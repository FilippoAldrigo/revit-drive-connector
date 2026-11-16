# Google Drive Connector for Autodesk Revit

[![Autodesk Revit](https://img.shields.io/badge/Revit-2025%20%7C%202026%20%7C%202027-blue.svg)](https://www.autodesk.com/products/revit/overview)
[![Platform](https://img.shields.io/badge/Platform-Windows%2010%20%2F%2011%20x64-0078D6.svg)](https://www.microsoft.com/windows)
[![Framework](https://img.shields.io/badge/.NET-10.0%20%2F%208.0%20WPF-512BD4.svg)](https://dotnet.microsoft.com/)
[![Language](https://img.shields.io/badge/Language-English%20%7C%20Italiano-informational.svg)]()
[![License: Proprietary Free-to-Use](https://img.shields.io/badge/License-Proprietary%20%7C%20Free%20to%20Use-success.svg)](LICENSE)
[![Google Drive](https://img.shields.io/badge/Cloud-Google%20Drive%20v3-4285F4.svg)](https://www.google.com/drive/)

**Google Drive Connector** is a **Cloud Worksharing and Multi-User Collaboration solution for Autodesk Revit (versions 2025, 2026, and 2027)** powered by Google Drive.

It enables distributed teams of architects, structural engineers, and BIM specialists to collaborate simultaneously on shared Central Models (`.rvt`) across remote locations without requiring costly *Autodesk BIM Collaborate Pro* (formerly BIM 360 Design) subscriptions, while guaranteeing robust file integrity, real-time element borrowing, and zero synchronization conflicts.

---

## Downloads & Releases

Download the latest version directly from the official [GitHub Releases](https://github.com/FilippoAldrigo/revit-drive-connector/releases) page:

* **[Download RevitDriveConnector_Setup.exe](https://github.com/FilippoAldrigo/revit-drive-connector/releases/latest/download/RevitDriveConnector_Setup.exe)** *(Standalone 1-click Windows Installer — auto-configures Revit 2025/2026/2027)*
* **[Download RevitDriveConnector_Installatore.zip](https://github.com/FilippoAldrigo/revit-drive-connector/releases/latest/download/RevitDriveConnector_Installatore.zip)** *(Portable binary archive for manual deployment or IT administration)*

---

## Why Google Drive Connector?

Standard cloud storage services (Google Drive Desktop, Dropbox, OneDrive) cannot natively support Revit Worksharing because simultaneous writes to central model delta files lead to severe database corruption, orphan pointers, and unrecoverable sync conflicts.

**Google Drive Connector** solves this through a distributed **Gatekeeper Anti-Desync Architecture** featuring:
- **Mutual Exclusion Distributed Locking:** Guarantees that only one user synchronizes with central (`STC`) at any given millisecond.
- **SHA-256 Checksum Delta Verification:** Prevents race conditions by verifying cloud deltas before Revit commits changes.
- **High-Speed Local SSD Caching (`C:\RevitDriveCache`):** Revit operates directly on local SSD speed, eliminating cloud lag and file locks during normal drafting and 3D navigation.
- **Clock Skew Protection:** Built-in NTP/UTC time reconciliation protects teams working across different timezones or out-of-sync system clocks.

---

## Core Features

### 1. Corruption Prevention & Exclusive Lock Engine (Gatekeeper Anti-Desync)
* **Coordinated Lock Acquisition:** Before any user can synchronize with central (`Synchronize with Central` / `STC`), the plugin negotiates a distributed mutual-exclusion lock.
* **Integrity Gatekeeper:** Verifies SHA-256 checksums and ensures all delta files have finished downloading to disk before allowing Revit to write back to the central repository.
* **Clock Skew Compensation:** Built-in NTP/UTC network time synchronization with configurable safety margins (default +/- 15 seconds) protects against desynchronized system clocks.

### 2. High-Performance Local SSD Cache
* Revit never operates directly on slow cloud virtual drives.
* Local working copies reside on your fast local SSD storage (`C:\RevitDriveCache`), ensuring instantaneous file open, rapid local saves, and smooth 3D viewport navigation.

### 3. One-Click Team Sharing via Google Email
* Invite collaborators directly from within the Revit user interface by typing their Google email address.
* The plugin automatically assigns Google Drive folder permissions via the **Google Drive API v3**.
* Team members open Revit, click **Open from Google Drive**, and immediately see shared projects under their cloud project catalog.

### 4. Real-Time Element Borrowing (Cloud Worksharing Monitor)
* Request, grant, and deny editing permissions on individual BIM objects (walls, doors, windows, families) in real time.
* Includes instant non-intrusive **Live Toast** popup notifications in the bottom corner of the screen so team members can approve requests with one click without needing to execute a full model synchronization.

### 5. Automated External Link Consolidation & Healing (CAD, IFC, RVT)
* External links are automatically consolidated into a shared cloud `..\Links\` directory using relative paths.
* Eliminates the common *"Link Not Found"* errors caused by different drive letters or distinct user profile directory paths across the team.

### 6. Fully Integrated Revit Ribbon UI & Multilingual Support
* Native **Drive Connector** Ribbon tab with custom vector icons (32x32 and 16x16) matching the Autodesk Revit Light and Dark visual themes.
* Built-in multi-language localization supporting **English** and **Italian**, with automatic language detection from Autodesk Revit or the Windows operating system, plus manual selection in Cloud Settings.
* Recent files integration automatically pins active cloud projects into the standard Revit Home Page.

---

## Revit Ribbon Commands

| Command | Description |
| :--- | :--- |
| **Open from Google Drive** | Browse shared central models on the cloud and automatically initialize the synchronized local model. |
| **Publish to Drive** | Activates Worksharing on the active document and publishes it as a shared Central Model on Google Drive. |
| **Share Project** | Grant project access to colleagues by entering their Google email address. |
| **Sync with Drive STC** | Safely synchronizes model changes with exclusive lock protection and delta verification. |
| **Status & Active Users** | Displays cloud connection status, active team members, lock details, and emergency unlock options. |
| **Request Element Borrow** | Requests editing permission from the current owner for the selected BIM elements. |
| **Borrowing Center** | Review, grant, deny, or relinquish element borrowing requests across the team. |
| **Manage Links** | Consolidates and relativizes CAD, IFC, and Revit external links into the cloud Links folder. |
| **Google Account** | Sign in and configure your Google Drive OAuth 2.0 account credentials. |
| **Cloud Settings** | Configure cloud storage directory paths, local SSD cache folders, and interface language. |

---

## Quick Installation & Setup Guide

### 1. Installation
1. Download and run **`RevitDriveConnector_Setup.exe`**.
2. The installer will automatically:
   - Detect installed Autodesk Revit versions (2025, 2026, 2027).
   - Install the background Daemon and Server coordination services.
   - Deploy `.addin` manifest files into `%APPDATA%\Autodesk\Revit\Addins\<Year>\`.

### 2. Google OAuth 2.0 Configuration
To enable the Google Drive v3 synchronization:
1. Open Autodesk Revit and navigate to the **Drive Connector** Ribbon tab.
2. Click **Google Account** -> **Credentials Setup**.
3. Provide your `credentials.json` (or paste Client ID & Client Secret) obtained from your [Google Cloud Console](https://console.cloud.google.com/) (Application type: *Desktop App*).
4. Click **Sign in with Google** to complete the browser authorization.

> **Note for Project Teams / Testing:**  
> When running the Google Cloud project in *Testing mode*, add your team members' Google accounts under **Test Users** in the Google Cloud Console (*OAuth consent screen*). They can immediately authenticate without waiting for Google app verification.

---

## Technical Architecture

The solution is engineered in **.NET 10 / .NET 8 (C#)** with a high-performance modular structure:

* **`RevitDriveConnector.Addin`**: Native Revit add-in library interacting with Autodesk Revit DB/UI APIs and AdWindows.
* **`RevitDriveConnector.Core`**: Common core providing IPC Named Pipes, localization engine (`Loc.cs`), SHA-256 hash validation, NTP time synchronization, and lock models.
* **`RevitDriveConnector.Daemon`**: Background Windows service managing Google Drive API v3 communication, cloud delta synchronization, and lock state.
* **`RevitDriveConnector.Server`**: Optional lightweight coordinator server for ultra-low-latency LAN worksharing environments.
* **`RevitDriveConnector.Installer`**: Standalone WPF installation executable adhering to modern Windows and Autodesk Human Interface Guidelines.

---

## Privacy Policy & Credentials Security

* All OAuth tokens and refresh tokens are stored exclusively on your local machine using encrypted Windows credential mechanisms (`%APPDATA%\RevitDriveConnector`).
* No credentials, access tokens, project geometries, or proprietary BIM data are ever transmitted to third-party tracking servers.
* For complete details on data processing and Google API compliance, please read our [Privacy Policy (PRIVACY.md)](PRIVACY.md).

---

## Authors & Co-Authorship

* **Filippo Aldrigo** — Author, Lead Architect & Developer
* **Google Gemini** — Co-Author, Software Engineering & Technical Collaboration

---

## License & Disclaimers

* **License:** Copyright (c) 2026 Filippo Aldrigo. All rights reserved. Proprietary software. **Free to use for personal, educational, and professional BIM design.** Commercial resale, sublicensing, redistribution, or packaging by third parties for profit without express written authorization is strictly prohibited. See [LICENSE](LICENSE) for details.
* **Disclaimer:** *Google Drive Connector for Autodesk Revit is an independent software solution and is not affiliated with, sponsored by, or endorsed by Autodesk, Inc. or Google LLC. Autodesk and Revit are registered trademarks of Autodesk, Inc. Google and Google Drive are registered trademarks of Google LLC.*
