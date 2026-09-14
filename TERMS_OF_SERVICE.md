# Terms of Service — V-Notch

**Effective Date:** September 14, 2026  
**Application Version:** 1.9.2+  
**Developer:** rainaku  
**Repository:** [github.com/rainaku/V-Notch](https://github.com/rainaku/V-Notch)  
**Contact:** [github.com/rainaku/V-Notch/issues](https://github.com/rainaku/V-Notch/issues)  

---

## 1. Acceptance of Terms

Welcome to **V-Notch**!

By downloading, installing, compiling, executing, accessing, or using V-Notch (the "Application", "Software", or "Service"), you ("User", "you", or "your") agree to be bound by these Terms of Service ("Terms") and our [Privacy Policy](PRIVACY_POLICY.md).

If you do not agree to these Terms or the Privacy Policy, you must not download, install, or use V-Notch. If you have already installed V-Notch, you must immediately discontinue its use and uninstall it from your device.

You affirm that you are either at least 18 years of age, or an emancipated minor, or possess legal parental or guardian consent, and are fully able and competent to enter into the terms, conditions, obligations, affirmations, representations, and warranties set forth in these Terms.

---

## 2. Nature of Software & Open-Source Licensing

### 2.1 License Grant
V-Notch is free and open-source software distributed under the terms of the **Apache License, Version 2.0** ("Apache License"). You may inspect, modify, fork, and distribute the source code and compiled binaries in accordance with the rights and obligations granted under the Apache License. A copy of the license is included in the project repository at [LICENSE](LICENSE).

### 2.2 Relationship Between These Terms and the Apache License
These Terms govern your use of the compiled application binaries, official releases, associated documentation, and interactions with integrated network services and third-party platforms. In the event of any direct conflict between these Terms and the Apache License regarding the copyright licensing and redistribution of the source code, the **Apache License 2.0** shall take precedence to the extent of such conflict.

### 2.3 Strict Prohibition of Redistribution Under Your Own Name
- **NO REDISTRIBUTION UNDER YOUR OWN NAME:** You are **STRICTLY PROHIBITED** from redistributing, repackaging, rebranding, reselling, or republishing the V-Notch software (in whole or substantial part) under your own name, organization name, or commercial brand in any manner that claims, suggests, or misleads others into believing that you are the original author, creator, or copyright holder of V-Notch.
- **Mandatory Attribution Retention:** All original copyright notices (including `Copyright © 2026 rainaku`), links to the official repository at [github.com/rainaku/V-Notch](https://github.com/rainaku/V-Notch), and the Apache License 2.0 notices must remain intact and unmodified across all source files, compiled binaries, user interfaces, and documentation.
- **Rules for Forks and Derivative Works:** If you fork or modify the codebase as permitted by the Apache License 2.0, you must:
  1. Prominently and conspicuously credit **rainaku** as the original creator of V-Notch;
  2. Clearly document all modifications and alterations made to the codebase;
  3. Use a completely distinct and non-confusing name for your fork or derivative project. You may not use the name "V-Notch" or confusingly similar variations as the title or brand of your project without prior written authorization.

---

## 3. Local-First Architecture & System Permissions

V-Notch is designed with a strict **local-first** architecture. The application operates primarily on your machine and does not run any private backend servers, user accounts, telemetry, or remote user tracking.

To deliver desktop dynamic notch capabilities, ambient media widgets, and utility tools, V-Notch requires access to certain Windows operating system APIs:

1. **Media Sessions (Windows SMTC):** Reads active media metadata (song title, artist, album art, playback position, play/pause states) from supported media players and web browsers.
2. **Audio Endpoints (Core Audio):** Reads and adjusts system master volume and individual application audio session levels for the multi-app mixer.
3. **Screen Sampling (DirectX 11 / DXGI / Magnification):** Samples pixel data directly beneath the notch area in local GPU/CPU memory to render realistic Liquid Glass optical refraction and blur. Captured frames are never recorded, saved to disk, or transmitted over any network.
4. **Camera Preview (DirectShow / MediaFoundation):** Provides an optional local mirror preview. Camera frames are rendered directly to the screen and are never stored or transmitted.
5. **File Shelf:** Permits drag-and-drop staging of local files and shortcuts. File paths are stored locally in application settings.
6. **Spotlight Launcher:** Performs local searches of installed applications and files using Windows Search or Everything IPC (`Alt + Space`).
7. **Hardware Telemetry (Performance Counters / DXGI):** Reads real-time local CPU, RAM, and GPU load metrics.
8. **Bluetooth & Battery:** Queries connected Bluetooth peripheral status and battery percentages.
9. **Clipboard Listener:** Detects copy events to display transient visual preview banners on the notch. Clipboard contents are never logged or uploaded.

By running V-Notch, you grant the application permission to interface with these local system APIs solely to facilitate these features.

---

## 4. Third-Party Services, APIs & Trademark Disclaimers

### 4.1 Independent Third-Party Services
V-Notch integrates optional or functional client-side connections to certain third-party services. You acknowledge and agree that:
- These services are independent entities not operated, controlled, or owned by the developer of V-Notch.
- Your use of third-party features may subject you to the respective third party's terms of service, acceptable use policies, and privacy policies:
  - **Spotify:** Spotify Terms of Service, User Agreement, and Developer Terms.
  - **YouTube & Google:** YouTube Terms of Service and Google Privacy Policy.
  - **LRCLIB / lrc mux:** LRCLIB service conditions and lrc mux aggregator terms.
  - **Open-Meteo & ipwho.is:** Open-Meteo Terms and ipwho.is Terms.
  - **GitHub:** GitHub Terms of Service (for update checks and release downloads).
  - **SoundCloud:** SoundCloud Terms of Use.

### 4.2 Spotify Canvas Integration
V-Notch provides an optional feature allowing users to display Spotify Canvas video loops by connecting their Spotify account via Microsoft Edge WebView2 and storing the `sp_dc` session cookie locally (encrypted via Windows DPAPI).
- **Unofficial Integration:** V-Notch is an unofficial third-party client tool. It is **NOT** endorsed, certified, or affiliated with Spotify AB or any of its affiliates.
- **User Responsibility:** You are solely responsible for your use of Spotify credentials and Canvas retrieval. You acknowledge that accessing Spotify services through unofficial means may carry risks, including potential restrictions or actions on your Spotify account under Spotify's User Guidelines. The developer of V-Notch assumes no liability whatsoever for any account actions, penalties, or service disruptions imposed by Spotify.

### 4.3 YouTube Data & Artwork Scraping
When fetching metadata or thumbnails from YouTube, V-Notch utilizes public endpoints, oEmbed, or an optional user-supplied YouTube Data API key. Users supplying their own YouTube API keys are solely responsible for complying with Google's API Quotas and Developer Policies.

### 4.4 Availability and Modifications of External Services
Third-party APIs and web services may change, rate-limit, alter their structure, or terminate access at any time without notice. The developer makes **no warranty or guarantee** that any online feature (such as lyrics lookup, artwork search, weather forecasts, or Canvas streaming) will remain functional, continuous, or uninterrupted.

### 4.5 Third-Party Trademark Disclaimer
All product names, logos, brands, trademarks, and registered trademarks mentioned in the application, documentation, or marketing are property of their respective owners.
- "macOS", "Dynamic Island", "Apple", "iPhone", and "Apple Music" are trademarks or registered trademarks of Apple Inc.
- "Windows", "Microsoft", "DirectX", and "Edge" are trademarks or registered trademarks of Microsoft Corporation.
- "Spotify" and "Canvas" are trademarks or registered trademarks of Spotify AB.
- "YouTube" and "Google" are trademarks or registered trademarks of Google LLC.
- "SoundCloud" is a trademark of SoundCloud Global Limited & Co. KG.

Reference to any third-party products, services, trademarks, or visual designs is made **strictly for nominative, descriptive, and compatibility identification purposes** and does not constitute or imply any endorsement, sponsorship, association, or affiliation by or with the trademark holders.

---

## 5. Acceptable Use & Prohibited Conduct

You agree to use V-Notch only for lawful purposes and in accordance with these Terms. You agree **NOT** to:

1. **Violate Applicable Laws:** Use the Software for any purpose that violates any local, national, or international law, statute, ordinance, or regulation.
2. **Abuse Third-Party Services:** Use the Software or modified builds thereof to launch denial-of-service (DoS) attacks, scrape at abusive frequencies, bypass rate limits, or intentionally disrupt third-party providers (including GitHub, Spotify, LRCLIB, Open-Meteo, or YouTube).
3. **Malicious Distribution:** Distribute modified, backdoored, trojanized, or infected copies of V-Notch while representing them as official releases or using the name "V-Notch" to deceive end users.
4. **Infringe Intellectual Property:** Use the Software to stage, transmit, or display content that infringes upon the copyright, trademark, patent, trade secret, or other proprietary rights of any third party.
5. **Circumvent Security:** Modify the application to disable cryptographic integrity verification (such as ECDSA update manifest validation) for malicious software injection purposes.
6. **Plagiarism & Misrepresentation of Ownership:** Stripping copyright or author attribution, replacing the author's name with your own, redistributing V-Notch under your own name, or falsely representing yourself as the author, owner, or creator of V-Notch.

---

## 6. Updates & Network Integrity

### 6.1 Automatic and Manual Update Checks
V-Notch may periodically contact GitHub Releases API to check if an update is available. In-app update downloads and executions occur only upon your confirmation.

### 6.2 Cryptographic Verification
Starting with version 1.9.2, official update manifests are cryptographically signed using an embedded ECDSA P-256 public key. V-Notch verifies file size, SHA-256 hash, and digital signature before executing update installers.

### 6.3 Strict Local-Only Mode
If you wish to prohibit all outbound network requests entirely, V-Notch provides a **Strict Local-Only Mode** in the Privacy Settings. When enabled, all update checks, online lyrics queries, album art lookups, Spotify Canvas requests, and weather queries are unconditionally blocked.

---

## 7. Disclaimer of Warranties ("AS IS")

TO THE MAXIMUM EXTENT PERMITTED BY APPLICABLE LAW:

1. **"AS IS" BASIS:** V-NOTCH IS PROVIDED TO YOU "AS IS", "WITH ALL FAULTS", AND "AS AVAILABLE", WITHOUT WARRANTY OF ANY KIND, EITHER EXPRESS, IMPLIED, STATUTORY, OR OTHERWISE.
2. **NO IMPLIED WARRANTIES:** THE DEVELOPER, AUTHORS, AND CONTRIBUTORS SPECIFICALLY DISCLAIM ALL WARRANTIES OF ANY KIND, INCLUDING BUT NOT LIMITED TO IMPLIED WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE, TITLE, ACCURACY, SYSTEM INTEGRATION, QUIET ENJOYMENT, AND NON-INFRINGEMENT.
3. **NO GUARANTEE OF COMPATIBILITY OR RELIABILITY:** THE DEVELOPER DOES NOT WARRANT THAT:
   - THE APPLICATION WILL MEET YOUR SPECIFIC REQUIREMENTS OR EXPECTATIONS;
   - THE APPLICATION WILL OPERATE UNINTERRUPTED, TIMELY, SECURE, OR ERROR-FREE;
   - DEFECTS, GLITCHES, OR SYSTEM CONFLICTS (INCLUDING GRAPHICS DRIVER INTERACTION, DISPLAY OVERLAYS, TASKBAR INTERACTIONS, OR COMPATIBILITY WITH WINDOW MANAGERS LIKE MYDOCKFINDER) WILL BE CORRECTED;
   - THIRD-PARTY MEDIA OR STREAMING INTEGRATIONS WILL REMAIN COMPATIBLE OR ACCESSIBLE OVER TIME.

YOUR USE OF THE APPLICATION IS ENTIRELY AT YOUR SOLE RISK.

---

## 8. Limitation of Liability

TO THE FULLEST EXTENT PERMITTED BY APPLICABLE LAW, IN NO EVENT SHALL THE DEVELOPER (RAINAKU), CONTRIBUTORS, COPYRIGHT HOLDERS, OR AFFILIATED PARTIES BE LIABLE FOR ANY:

1. **INCIDENTAL, CONSEQUENTIAL, INDIRECT, SPECIAL, OR PUNITIVE DAMAGES;**
2. **LOSS OF PROFITS, REVENUE, DATA, REPUTATION, BUSINESS OPPORTUNITIES, OR GOODWILL;**
3. **SYSTEM CRASHES, HARDWARE OVERHEATING, OPERATING SYSTEM INSTABILITY, CORRUPTION OF FILES, OR DISPLAY ARTIFACTS;**
4. **ACTIONS TAKEN BY THIRD-PARTY PLATFORMS, INCLUDING THE SUSPENSION, RESTRICTION, OR TERMINATION OF YOUR SPOTIFY, YOUTUBE, OR OTHER ACCOUNTS RESULTING FROM THE USE OF UNOFFICIAL CLIENT SCRAPING OR CANVAS RETRIEVAL;**
5. **ANY MATTERS BEYOND THE REASONABLE CONTROL OF THE DEVELOPER.**

THESE LIMITATIONS APPLY REGARDLESS OF THE LEGAL THEORY (WHETHER IN CONTRACT, TORT, STRICT LIABILITY, NEGLIGENCE, WARRANTY, OR OTHERWISE), EVEN IF THE DEVELOPER HAS BEEN ADVISED OF THE POSSIBILITY OF SUCH DAMAGES.

---

## 9. Indemnification

You agree to defend, indemnify, and hold harmless the developer, contributors, and maintainers of V-Notch from and against any and all claims, damages, obligations, losses, liabilities, costs, or debt, and expenses (including but not limited to legal fees) arising from:
- Your use of or access to the Application;
- Your violation of any provision of these Terms;
- Your violation of any third-party right, including without limitation any intellectual property, privacy, or contractual right (such as third-party platform terms of service);
- Any claim that files, media, or data handled through your copy of the Application caused damage to a third party.

---

## 10. Privacy Policy

Your privacy is paramount. V-Notch's data handling practices are transparently documented in our [Privacy Policy](PRIVACY_POLICY.md) (and [Chính Sách Bảo Mật Tiếng Việt](PRIVACY_POLICY_VI.md)).

By using V-Notch, you acknowledge that you have read and understood the Privacy Policy, which explains how local data is managed and what minimal network interactions occur.

---

## 11. Severability & Entire Agreement

If any provision of these Terms is found to be unlawful, void, or for any reason unenforceable by a court of competent jurisdiction, then that provision shall be deemed severable from these Terms and shall not affect the validity and enforceability of any remaining provisions.

These Terms, together with the [Privacy Policy](PRIVACY_POLICY.md) and the [Apache License 2.0](LICENSE), constitute the entire agreement between you and the developer regarding your use of V-Notch.

---

## 12. Modifications to Terms

The developer reserves the right, at its sole discretion, to modify, update, or replace these Terms at any time. When revisions are made:
- The "Effective Date" at the top of this document will be updated.
- The revised document will be published to the official repository at [github.com/rainaku/V-Notch](https://github.com/rainaku/V-Notch).
- Significant changes may be summarized in application release notes or changelogs.

Your continued download, installation, or use of V-Notch following the posting of any revised Terms constitutes your full acceptance of the updated Terms.

---

## 13. Governing Law & Jurisdiction

These Terms shall be interpreted and governed in accordance with applicable laws, without regard to its conflict of law principles. Any dispute, claim, or controversy arising out of or relating to these Terms or the Application shall be addressed through good-faith informal communication via the project's public issue tracker or developer contact channels prior to pursuing any formal legal remedies.

---

## 14. Contact Information

If you have questions, feedback, bug reports, or legal inquiries concerning these Terms of Service, please reach out via:

- **GitHub Issues:** [https://github.com/rainaku/V-Notch/issues](https://github.com/rainaku/V-Notch/issues)
- **Developer Website / Portfolio:** [https://rainaku.id.vn](https://rainaku.id.vn)
- **Official Repository:** [https://github.com/rainaku/V-Notch](https://github.com/rainaku/V-Notch)
