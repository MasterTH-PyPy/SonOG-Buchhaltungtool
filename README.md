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
6. **Kontieren** (Reiter 2) – je Buchung Personenkonto, Sachkonto(en), BU-Schlüssel, Splitbuchungen; Vorschläge aus Regeln.
7. **Taxpool-Importdatei** – DATEV-Buchungsstapel (`EXTF_….csv`), den Taxpool-Buchhalter einliest.
8. **Sollstellung** (eigener Reiter) – Ausgangsrechnungen eines Monats als Forderung auf die Debitoren.

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

Die **erste Belegnummer** ist im Hauptfenster einstellbar (damit es keine Überschneidungen mit anderen Nummern gibt): liegt der Zähler
darunter, wird er hochgesetzt, liegt er darüber, läuft er einfach weiter.

**Manuelle Zuordnungen bleiben erhalten:** Wer einen Beleg von Hand zuordnet, einen Vorschlag übernimmt oder einen Beleg entfernt/abwählt,
dessen Eingriff wird je Auszug unter `%AppData%\SonOG-Buchhaltung\zuordnungen` gespeichert und nach jedem Einlesen wieder angewendet
(erkannt über Datum, Betrag und Text der Buchung, nicht über die Belegnummer). Der Button **Dokumente neu einlesen** liest Rechnungs-,
Eingangs- und Mail-Ordner neu und gleicht neu ab, ohne den Kontoauszug und die Nummern anzufassen. **Einlesen / Vorschau** lädt den
Auszug komplett neu, wendet die gespeicherten Eingriffe aber ebenfalls wieder an.

Rechts neben der Tabelle zeigt die **PDF-Vorschau** die Kontoauszugsseite der markierten Buchung sowie ihre Belege und Rechnungsvorschläge
(Seitenwechsel, Zoom, „Extern öffnen“, „Zuordnung entfernen“). Die Anzeige nutzt die in Windows eingebaute PDF-Funktion.

Die Nummern merkt sich `nummern.json` unter `%AppData%\SonOG-Buchhaltung`. Derselbe Auszug bekommt bei erneutem Export
dieselben Nummern. Ändert sich die Buchungsanzahl, wird der Export verweigert, bis die Nummern mit **Nummern freigeben**
zurückgenommen wurden. Dort steht auch, welche Rechnungen schon bezahlt sind (Doppelzahlungen werden erkannt).

> **Signatur:** Der Sparkassen-Auszug ist qualifiziert signiert. Die gestempelte Kopie ist es nicht mehr –
> das unveränderte Original separat archivieren.

## Schritt 2: Kontieren

Nach dem Einlesen (Schritt 1) steht jede Buchung im Reiter **2 · Kontieren** mit einem Vorschlag. Gelb = Vorschlag,
grün = bestätigt, rot = Fehler (z. B. Summe der Zeilen ≠ Buchungsbetrag, Sachkonto fehlt). Alles ist je Buchung änderbar und
wird sofort gespeichert (`kontierung\<Auszug>.json`); beim erneuten Einlesen desselben Auszugs bleibt es erhalten.

Drei Arten zu buchen (Doppik mit Personenkonten):

| Personenkonto | „Beleg einbuchen“ | Buchungssätze |
|---|---|---|
| keins | – | Bank an/gegen Sachkonto je Zeile (Bankentgelt, Finanzamt, Privat …) |
| Kreditor/Debitor | an | je Zeile: Sachkonto an Kreditor (bzw. Debitor an Erlös), Rechnungsdatum als Belegdatum; dann Zahlung: Kreditor an Bank |
| Debitor/Kreditor | aus | Ausgleich offener Posten: Bank an Debitor je Rechnung (Rechnungsnummer in Belegfeld 1, laufende Nummer in Belegfeld 2) |

**Splitbuchungen:** „+ Zeile (Rest)“ legt eine Zeile mit dem Restbetrag an, z. B. eine Amazon-Zahlung mit 100,00 auf 3400 und
50,00 auf 1800. Die Summe der Zeilen muss dem Betrag im Kontoauszug entsprechen.

**Vorschläge** (Reihenfolge): Kundenzahlung mit erkannten Rechnungen → Debitor, je Rechnung eine Zeile · erste passende Regel
(Stichwort im Buchungstext, Kategorie, Richtung) · Personenkonto per Suchbegriff mit Standard-Sachkonto · sonst offen.
„Als Regel speichern …“ macht aus der aktuellen Kontierung eine Regel; „Vorschläge neu“ wendet neue Regeln auf alle noch
nicht bestätigten und nicht von Hand bearbeiteten Buchungen an.

**Aus Taxpool einlesen …** übernimmt CSV-Exporte aus Taxpool, Spalten werden über die Überschrift erkannt:
Kontenplan (Konto + Bezeichnung → Kontonamen, Personenkonten → Debitoren/Kreditoren) und Buchungsvorlagen
(Konto + Gegenkonto + BU + Buchungstext → Vorlage, die man je Buchung anwenden oder mit Stichwort zur Regel machen kann).

## Schritt 3: Importdatei für Taxpool

**3 · Taxpool-Importdatei erzeugen …** schreibt `EXTF_Kontoauszug_<Auszug>.csv` (DATEV-Buchungsstapel, Version 700/13,
Windows-1252) in den Ausgabeordner, dazu `Personenkonten_….csv` mit den verwendeten Debitoren/Kreditoren. Voraussetzung:
Die Nummern sind in Schritt 1 endgültig vergeben (Export). Bei Fehlern wird keine Datei geschrieben; ein zweiter Export
desselben Auszugs wird gewarnt (doppelter Import). Die Buchungen werden nicht festgeschrieben.

## Sollstellung der Ausgangsrechnungen

Eigener Reiter, unabhängig vom Kontoauszug: Monat wählen, **Rechnungen laden** (CAO-Ordner aus Schritt 1). Jede Rechnung
des Monats wird Debitor an Erlöskonto (Standard 8400, Automatikkonto) mit Rechnungsdatum und Rechnungsnummer gebucht.
Debitoren werden über den Empfängernamen gefunden oder mit **Debitor anlegen …** angelegt. Exportierte Rechnungen merkt sich
`sollstellung.json`; sie werden grau angezeigt und nicht noch einmal übergeben (**Monat freigeben …** nimmt das zurück).

## Einstellungen (`buchhaltung.json`, `personenkonten.json`)

Standard ist SKR03: Bank 1200, Debitoren 10000–69999, Kreditoren 70000–99999, Sachkontenlänge 4, Berater 1001, Mandant 1.
Bei SKR04 Kontenrahmen `"04"` und Bank `1800` eintragen, Regeln anpassen. Button **Einstellungen / Regeln öffnen** im Reiter 2.
`rechnungsnummerInBelegfeld1BeiAusgleich` steuert, wo beim OP-Ausgleich die Rechnungsnummer steht.

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

## Icon

`tools/make_icon.py` (Pillow) erzeugt `src/SonOG.Buchhaltung.App/Resources/app.ico` in allen Windows-Größen.

## Stand / bekannte Lücken

- Skonto/Differenzen bei Kundenzahlungen: der Rest bleibt auf dem Debitor und wird in Taxpool ausgebucht (oder Zeile aufteilen).
- Sollstellung mit mehreren Steuersätzen je Rechnung: nur ein Erlöskonto je Rechnung.
- Spaltennamen der Taxpool-Exporte sind über Synonyme erkannt; bei einem anderen Layout eine Beispieldatei prüfen.

- Der Parser für den Sparkassen-Kontoauszug ist an einem echten Auszug (87 Buchungen, Saldo stimmt) geprüft.
- **Das Layout der CAO-Rechnungs-PDFs wurde noch nicht gesehen.** Betrag und Empfänger werden über die Einstellungen
  im Abschnitt `rechnung` gefunden und müssen an einer echten Rechnung kalibriert werden. Betrag nicht lesbar →
  Status „Rechnungsbetrag nicht lesbar“, es wird nie geraten.
- Amazon-Belege: Bestellnummer **und** Betrag müssen passen. Der Amazon-Ordner darf die Wochenexporte enthalten
  (ZIP-Dateien oder entpackte Ordner je Bestellung, auch mehrere Wochen gemischt). Rechnungen und Gutschriften
  (Rechnungskorrektur) werden zugeordnet; die „Übersicht zur Bestellung“ ist keine Rechnung und dient nur als
  Betragsprüfung. Rechnungen von Marktplatz-Verkäufern ohne lesbaren Betrag werden nur zugeordnet, wenn die
  Bestellübersicht denselben Betrag zeigt (Hinweis „bitte prüfen“). Das Layout wurde an einem Wochenexport geprüft,
  die PdfPig-Textausgabe aber noch nicht auf Windows. Auch der Export „von/bis“ (eine Datei je Rechnung, Name
  `JJJJMMTT_Tax Invoice_<Bestellnummer>.pdf`) wird gelesen; die Bestellnummer wird zusätzlich aus dem Dateinamen genommen.
  Steht der Betrag nur im Belegtext (nicht als „Zahlbetrag“ erkannt), wird bei eindeutiger Bestellnummer trotzdem
  zugeordnet (Hinweis „bitte prüfen“). Als letzter Versuch wird ein Teil der Bestellnummer (eine 7-stellige Gruppe)
  rekursiv im Dateipfad und Belegtext des Eingangsordners gesucht (ZIPs werden vorher entpackt); der Betrag muss passen.
- **Eingangsordner, sonstige Lieferanten:** Zugeordnet wird nur, wenn der **Betrag auf den Cent** im PDF steht
  und dazu die im Buchungstext genannte Rechnungsnummer oder der Shop-/Verkäufername. Passt nur die Rechnungsnummer, aber nicht der
  Betrag, oder nur der Betrag ohne Namen, gibt es einen Hinweis, aber keine Zuordnung.
- **Scans / OCR:** Eingescannte PDFs ohne Textebene (Eingangsordner) werden mit der in Windows eingebauten Texterkennung
  (Windows.Media.Ocr, Sprachpaket Deutsch nötig) gelesen, die ersten 3 Seiten. Treffer aus OCR tragen den Hinweis
  „Scan per OCR gelesen, Zahlen genau kontrollieren“. Ist die OCR nicht verfügbar, erscheint eine Warnung. **Nicht kompiliert/getestet**
  (kein Scan zur Hand); das Infrastructure-Projekt ist deshalb jetzt `net8.0-windows10.0.19041.0`.
- **Mail-Anhänge:** Button „Mail-Postfächer …“ (IMAP: web.de, Gmail mit App-Passwort, Roundcube-Server) und Häkchen
  „Mails abrufen“. Beim Einlesen werden **alle PDF-Anhänge** der Mails im Zeitraum des Auszugs (Standard 60 Tage vor bis 30 Tage nach den
  Buchungen, im Fenster „Mail-Postfächer“ einstellbar) geladen (nur lesend, nur die Anhänge) nach `%AppData%\SonOG-Buchhaltung\mail` und wie der Eingangsordner
  nach **Inhalt** abgeglichen (Amazon: Bestellnummer + Betrag; sonstige: Betrag + Rechnungsnummer oder Name; Scans per OCR).
  Der Mailserver wird nicht nach Absender/Betreff gefragt, entscheidend ist allein der PDF-Inhalt. Mails von Amazon werden übersprungen
  (Amazon kommt aus dem Wochenexport im Eingangsordner). Alles mit „bitte prüfen“; vor dem Druck lassen sich falsche Belege abwählen. Protokoll:
  `%AppData%\SonOG-Buchhaltung\mail-protokoll.txt`. Passwörter liegen DPAPI-verschlüsselt in `einstellungen.json`.
  Der Mail-Code (MailKit) ist nicht gegen ein echtes Postfach getestet.
- Echte Kontoauszüge, Rechnungen und Excel-Ausgaben sind per `.gitignore` vom Repo ausgeschlossen.
