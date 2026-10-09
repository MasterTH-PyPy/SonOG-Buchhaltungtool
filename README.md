# SonOG Buchhaltung

WinForms-Werkzeug (.NET 8) für die Firmenbuchhaltung:

1. **Kontoauszug nummerieren** – Sparkasse-Kontoauszug (PDF) einlesen, jede Buchung bekommt eine laufende Nummer
   (z. B. `2026-0042`), die links neben die Buchungszeile in eine **Kopie** des Auszugs gestempelt wird.
2. **Kundenzahlungen abgleichen** – Rechnungsnummern im Verwendungszweck werden mit den Ausgangsrechnungen
   (CAO-Rechnungsordner) verglichen: Betrag, Empfänger, Sammelzahlungen, Zahlendreher.
3. **Amazon-Belege zuordnen** – über die Bestellnummer (`305-1234567-1234567`).
4. **Beleg stempeln und Druckpaket** – die laufende Nummer wird auf die Rechnung/den Beleg gestempelt; ein PDF
   mit Kontoauszug und allen Belegen wird zusammengestellt - **rückwärts**: letzte Auszugsseite, dann deren Belege,
   dann die vorletzte Seite mit ihren Belegen usw. Die Anlageseite(n) hinter der letzten Buchungsseite
   (Entgeltabrechnung) bekommen die Nummer der Entgelt-Buchung und werden direkt hinter deren Seite einsortiert. Der Button **Drucken (rückwärts) – Vorschau** exportiert und öffnet das PDF
   im Standard-PDF-Programm; dort siehst du genau, was gedruckt würde, und druckst mit Strg+P.
5. **Excel-Liste** mit Status je Buchung, offene Rechnungen (für Mahnungen) und Zusammenfassung.

Beleglose Buchungen (Steuern, Bankentgelte, Privates …) bekommen ebenfalls eine Nummer, werden mit `BL` markiert und
verlangen keinen Beleg.

## Aufbau

| Projekt | Inhalt |
|---|---|
| `SonOG.Buchhaltung.Core` | Logik ohne Fremdpakete: Parser, Klassifizierung, Nummernvergabe, Abgleich, Namensvergleich |
| `SonOG.Buchhaltung.Infrastructure` | PDF lesen (PdfPig), stempeln/zusammenfügen (PDFsharp), Excel (ClosedXML), Ordner-Scan, `BuchhaltungService` |
| `SonOG.Buchhaltung.App` | WinForms-Oberfläche |
| `SonOG.Buchhaltung.Tests` | xUnit-Tests für Core (synthetische Daten, keine echten Kunden) |

```
dotnet build SonOG.Buchhaltung.sln
dotnet test tests/SonOG.Buchhaltung.Tests
dotnet run --project src/SonOG.Buchhaltung.App
```

## Ablauf in der Oberfläche

1. Kontoauszug, Ausgangsrechnungen (CAO), Eingangsrechnungen und Ausgabeordner wählen. Im Eingangsordner liegen Amazon-Wochenexporte
   (ZIP oder entpackt) und alle anderen Lieferantenrechnungen (PDF, auch in Unterordnern).
2. **Einlesen / Vorschau** – schreibt nichts (außer einem Cache). Die Tabelle zeigt je Buchung Nummer, Kategorie und
   Abgleich-Status. Grün = beleglos, gelb = bitte prüfen, rot = Problem.
3. Prüffälle bearbeiten: **Vorschlag übernehmen** (z. B. Zahlendreher), **Beleg zuordnen …** (von Hand), oder Regeln anpassen.
4. **Exportieren …** vergibt die Nummern endgültig und schreibt gestempelte Kopien, Excel und Druckpaket.

Die Nummern merkt sich `nummern.json` unter `%AppData%\SonOG-Buchhaltung`. Derselbe Auszug bekommt bei erneutem Export
dieselben Nummern. Ändert sich die Buchungsanzahl, wird der Export verweigert, bis die Nummern mit **Nummern freigeben**
zurückgenommen wurden. Dort steht auch, welche Rechnungen schon bezahlt sind (Doppelzahlungen werden erkannt).

> **Signatur:** Der Sparkassen-Auszug ist qualifiziert signiert. Die gestempelte Kopie ist es nicht mehr –
> das unveränderte Original separat archivieren.

## Abgleich von Kundenzahlungen

| Status | Bedeutung |
|---|---|
| OK | Rechnung gefunden, Betrag auf den Cent, Zahler passt zum Empfänger |
| Empfänger prüfen | Betrag stimmt, Zahler ≠ Rechnungsempfänger (z. B. Hausverwaltung) |
| Betrag weicht ab | Einzelrechnung, Differenz wird angezeigt (mit Skonto-Hinweis); evtl. Vorschlag, welche weitere Rechnung dazugehört |
| Summe stimmt nicht | Sammelzahlung: Summe der genannten Rechnungen ≠ Zahlung; es wird die Kombination angezeigt, die stimmen würde |
| Zahlendreher-Vorschlag | Nummer nicht im Ordner, aber eine ähnliche Nummer passt in Betrag und Name – oder der Betrag ist ein Zahlendreher |
| Rechnung nicht im Ordner | Genannte Nummer existiert nicht |
| Bereits bezahlt | Rechnung wurde schon in diesem oder einem früheren Auszug bezahlt |
| Keine Rechnungsnummer | Eingang ohne Bezug (ggf. als beleglos in `regeln.json` eintragen) |

Vorschläge (Zahlendreher, Betrag/Name) werden nie automatisch als Beleg übernommen.

## Regeln (`regeln.json`)

Wird beim ersten Start unter `%AppData%\SonOG-Buchhaltung` angelegt (Button **Regeln öffnen**):

- `belegloseStichwoerter` – Text im Verwendungszweck, z. B. `{ "stichwort": "AUTO LEASING", "grund": "Leasing privat" }`
- `belegloseTypen` – Buchungsart, z. B. `Entgeltabrechnung`
- `eigeneRechnungsnummerRegex` – Muster der eigenen Rechnungsnummern (Standard: `20` + 7 Ziffern, Gruppe 1)
- `nummernFormat` – z. B. `{year}-{n:0000}`
- `rechnung` – wie Betrag, Empfänger und Datum in den Rechnungs-PDFs gefunden werden:
  `betragStichwoerter` (die unterste Zeile mit einem dieser Wörter liefert den Betrag),
  `empfaenger` (Rechteck in Punkt, links oben = 0/0, in dem die Anschrift steht), `datumRegex`.

## Stand / bekannte Lücken

- Der Parser für den Sparkassen-Kontoauszug ist an einem echten Auszug (87 Buchungen, Saldo stimmt) geprüft.
- **Das Layout der CAO-Rechnungs-PDFs wurde noch nicht gesehen.** Betrag und Empfänger werden über die Einstellungen
  im Abschnitt `rechnung` gefunden und müssen an einer echten Rechnung kalibriert werden. Betrag nicht lesbar →
  Status „Rechnungsbetrag nicht lesbar“, es wird nie geraten.
- Amazon-Belege: Bestellnummer **und** Betrag müssen passen. Der Amazon-Ordner darf die Wochenexporte enthalten
  (ZIP-Dateien oder entpackte Ordner je Bestellung, auch mehrere Wochen gemischt). Rechnungen und Gutschriften
  (Rechnungskorrektur) werden zugeordnet; die „Übersicht zur Bestellung“ ist keine Rechnung und dient nur als
  Betragsprüfung. Rechnungen von Marktplatz-Verkäufern ohne lesbaren Betrag werden nur zugeordnet, wenn die
  Bestellübersicht denselben Betrag zeigt (Hinweis „bitte prüfen“). Das Layout wurde an einem Wochenexport geprüft,
  die PdfPig-Textausgabe aber noch nicht auf Windows.
- **Eingangsordner, sonstige Lieferanten:** Zugeordnet wird, wenn die im Buchungstext genannte Rechnungsnummer im PDF steht,
  oder wenn Betrag und Shop-/Verkäufername im PDF stehen. Nur der Betrag allein ergibt einen Hinweis, keine Zuordnung.
- **Scans / OCR:** Eingescannte PDFs ohne Textebene werden erkannt und gemeldet, aber noch nicht gelesen (keine OCR).
- **Mail-Suche:** Button „Mail-Postfächer …“ (IMAP: web.de, Gmail mit App-Passwort, Roundcube-Server) und Häkchen
  „Mails abrufen“. Nur für Buchungen **ohne Beleg**: Es wird in einem kleinen Zeitfenster (60 Tage vor bis 3 Tage nach der
  Buchung) nach einer Mail gesucht, deren Absender/Betreff den Shop-/Verkäufernamen aus dem Buchungstext enthält (bei Amazon
  auch die Bestellnummer im Text). Hat die Mail ein PDF, wird es nach `%AppData%\SonOG-Buchhaltung\mail` geladen und mit
  Rechnungsnummer (aus dem Buchungstext) und/oder Betrag abgeglichen; ohne Anhang steht ein Hinweis in der Tabelle.
  Alles nur lesend. Passwörter liegen DPAPI-verschlüsselt in `einstellungen.json`. Vor dem Druck zeigt ein Dialog alle
  Belege (Mail-Funde gelb) und lässt falsche abwählen. Der Mail-Code (MailKit) ist nicht gegen ein echtes Postfach getestet.
- Echte Kontoauszüge, Rechnungen und Excel-Ausgaben sind per `.gitignore` vom Repo ausgeschlossen.
