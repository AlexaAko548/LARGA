```markdown
# LARGA: Integrated Taxi Operations Management System

> **A Capstone Project for BLM Taxi (Talisay City, Cebu, Philippines)**  
> Developed by the Department of Computer, Information Sciences and Mathematics — University of San Carlos.

---

## 📌 Project Overview

**LARGA** is a centralized, cross-platform operations management platform designed to transition BLM Taxi from manual, paper-reliant processes to a secure, real-time digital system. The platform optimizes daily boundary cashiering, fleet maintenance, spare parts inventory, real-time contextual GPS tracking, and emergency driver safety.

The system is split into two primary client applications backed by a serverless cloud infrastructure:
1. **Driver & Tactical Manager Mobile App:** Built with **.NET MAUI** (Android-first deployment) for mobile shift logging, digital handover inspections, on-device OCR receipt scanning, and emergency alerting.
2. **Administrative Web Dashboard:** Built with **Blazor / ASP.NET Core** for fleet monitoring, boundary cashiering, debt ledgers, inventory control, and audit logs.

---

## 🛠️ Tech Stack & Architecture

| Component | Technology | Description |
| :--- | :--- | :--- |
| **Mobile Client** | .NET MAUI (C#) | Cross-platform mobile app for Drivers and tactical Manager use |
| **Web Dashboard** | ASP.NET Core / Blazor | Workstation dashboard for heavy administrative data and analytics |
| **Database & Auth** | Google Firebase | Cloud Firestore (NoSQL), Firebase Authentication, Security Rules |
| **Cloud Storage** | Cloud Storage for Firebase | Storage for handover inspection photos and fuel receipts |
| **Push Alerts** | Firebase Cloud Messaging (FCM) | Low-latency dispatch for SOS emergency alerts and shift reminders |
| **Mapping Engine** | MapLibre + MapTiler | Interactive live vector fleet maps and garage geofencing |
| **On-Device OCR** | Google ML Kit | Real-time text extraction for fuel receipts and odometer validation |
| **CI/CD & Tracking** | GitHub Actions & Jira | Automated PR build checks and Agile Sprint management |

---

## 🌿 Git Branching Strategy & Workflow Conventions

This repository strictly enforces a modified **GitFlow** branching strategy. Direct commits to `main` and `develop` are blocked by branch protection rules.


```

main (Production / Stable Releases)
│
├── release/v1.0.0
│     └── (Staging & Defense Builds)
│
develop (Active Integration Branch)
│
├── feature/EPIC01-driver-auth
├── feature/EPIC02-boundary-calc
└── hotfix/patch-odometer-ocr

```

### 1. Standard Branch Types & Naming

* **`main`**: Production-ready, fully tested releases. Code is deployed or presented from this branch.
* **`develop`**: The primary integration branch where completed sprint features are merged.
* **`feature/<ticket-id>-<short-description>`**: Feature branches branched off `develop` (e.g., `feature/REQ-4.1-driver-shift`, `feature/EPIC01-auth`).
* **`release/<version>`**: Release candidates prepared for sprint reviews, technical defenses, or staging tests (e.g., `release/v1.0.0`).
* **`hotfix/<issue-name>`**: Urgent patches branched directly off `main` to address critical defects.

---

### 2. Branch Protection Rules

The `main` and `develop` branches are protected with the following requirements:
* **Pull Request Required:** Direct `git push` is disabled. All code changes must come through a Pull Request (PR).
* **Code Review Approval:** Minimum of **1 required approving review** from a peer or Tech Lead before merge eligibility.
* **Passing CI Checks:** All GitHub Actions automated build and unit test workflows must pass.
* **Resolved Conversations:** All inline review comments and discussions must be formally resolved.
* **No Force Pushing:** `git push --force` is blocked on all protected branches.

---

### 3. Commit Message Conventions

All commit messages must follow standard Conventional Commits:

```bash
<type>(<scope>): <short summary in imperative mood>

# Examples:
feat(auth): implement Firebase role-based authentication claims
fix(ocr): resolve null pointer on low-contrast fuel receipt scans
chore(deps): update MapLibre NuGet package to v5.24.0
test(boundary): add unit tests for late penalty calculation logic
docs(readme): update branch protection guidelines and setup steps

```

Allowed Types: `feat`, `fix`, `docs`, `style`, `refactor`, `test`, `chore`.

---

## 💻 Local Development Setup

### Prerequisites

* **Microsoft Visual Studio 2022** (v17.14 or later)
* Workloads required:
* **.NET Multi-platform App UI development (.NET MAUI)**
* **ASP.NET and web development**


* **Android SDK & Android Device / Emulator** (API Level 31+)
* **Git** (v2.40+)

### Clone and Configure

1. Clone the repository:
```bash
git clone [https://github.com/](https://github.com/)<your-org>/LARGA.git
cd LARGA

```


2. Checkout the active development branch:
```bash
git checkout develop

```


3. Configure Firebase Credentials:
* Place the development `google-services.json` file inside the `src/Larga.Mobile/Platforms/Android/` directory.
* **Note:** Do not commit production Firebase service secrets to public remotes.


4. Open `LARGA.sln` in Visual Studio 2022, restore NuGet packages, select your target deployment target (e.g., Android Emulator or Web Project), and run the build.

---

## 📱 Core Modules

1. **Driver & Shift Management:** Shift scheduling, pre-shift/post-shift walk-around inspection checklists with photo verification.
2. **Boundary & Arrears Cashiering:** Boundary computation, rolling debt ledgers, and automated late penalties.
3. **Vehicle Maintenance Management:** Defect issue logging, repair work orders, and scheduled service tracking.
4. **Spare Parts Inventory:** Real-time stock counts, usage logs, and automated low-stock warnings.
5. **Contextual Real-Time GPS Tracking:** Live map monitoring with privacy-focused auto-cutoff upon shift clock-out.
6. **Fuel Monitoring & OCR Verification:** Real-time on-device receipt data extraction and mileage discrepancy analysis.
7. **Multi-Tiered SOS Emergency Protocol:** Driver panic button, Hostile Protocol (Shake-to-SOS), and Crash Protocol (G-force telemetry spike).
8. **Analytics & Audit Logs:** Revenue summaries, fleet uptime, and tamper-resistant system audit trails.

---

## 👥 Project Team

* **Maykaila Joan Arda** — Quality Assurance & Testing Lead
* **Luigi Adrian Hatamosa** — Software Developer
* **Karl Emmanuel Medina** — Software Developer
* **Alexa Rose Miñoza** — Software Developer / Project Manager

**Faculty Adviser:** Christine D. Bandalan, M.Eng.

**Institution:** University of San Carlos — DCRISM (August 2026)

```

```
