# Comprehensive BIR Tax & Compliance Research for Commercial POS

## Executive Summary
This document establishes the official regulatory, computational, and compliance baseline for implementing a commercial-grade Discount Management and Tax Calculation module within **Pos.Solution**. All formulas, validations, invoice requirements, and withholding rules set forth herein derive strictly from the National Internal Revenue Code of 1997 (NIRC), as amended (including the Ease of Paying Taxes [EOPT] Act, Republic Act No. 11976), and pertinent Bureau of Internal Revenue (BIR) Revenue Regulations (RR).

---

## 1. Official Legal References, URLs, and Effectivity Dates

| Issuance / Law | Subject / Title | Official Source URL | Effective Date |
| :--- | :--- | :--- | :--- |
| **Republic Act No. 9994** | Expanded Senior Citizens Act of 2010 | [Official Gazette](https://www.officialgazette.gov.ph/2010/02/15/republic-act-no-9994/) | February 15, 2010 |
| **Revenue Regulations No. 7-2010** | Implementing Tax Privileges of RA 9994 | [BIR Official Portal](https://www.bir.gov.ph/) | August 5, 2010 (15 days post-publication) |
| **Republic Act No. 10754** | An Act Expanding the Benefits and Privileges of PWDs | [Official Gazette](https://www.officialgazette.gov.ph/2016/03/23/republic-act-no-10754/) | April 12, 2016 (15 days post-publication) |
| **Revenue Regulations No. 5-2017** | Implementing Tax Privileges of RA 10754 | [NCDA Portal / BIR](https://ncda.gov.ph/) | April 26, 2017 (15 days post-publication) |
| **Republic Act No. 11976** | Ease of Paying Taxes (EOPT) Act | [Supreme Court E-Library](https://elibrary.judiciary.gov.ph/thebookshelf/showdocs/2/96948) | January 22, 2024 (15 days post-publication) |
| **Revenue Regulations No. 7-2024** | Consolidated Registration & Invoicing under EOPT | [BIR Official Portal](https://www.bir.gov.ph/) | April 27, 2024 (15 days post-publication) |
| **Revenue Regulations No. 11-2024**| Transitory Invoicing Deadlines & Extensions | [BIR CDN](https://bir-cdn.bir.gov.ph/BIR/pdf/RR%20No.%2011-2024.pdf) | June 28, 2024 (15 days post-publication) |
| **Revenue Regulations No. 6-2026** | Amending Section 6(B) of RR 7-2024 (CWT on Invoice) | [BIR CDN](https://bir-cdn.bir.gov.ph/BIR/pdf/RR%20No.%206-2026.pdf) | October 16, 2026 (15 days post-publication) |
| **Revenue Regulations No. 7-2026** | Burden-Sharing Agreement for Senior Citizen Discounts | [BIR CDN](https://bir-cdn.bir.gov.ph/BIR/pdf/RR%20No.%207-2026_redacted.pdf) | October 28, 2026 (15 days post-publication) |

---

## 2. Tax Classification & Business Registration
- **VAT-Registered Businesses:** Taxpayers whose gross annual sales exceed ₱3,000,000 (under NIRC Sec. 109(CC) as amended by the TRAIN Law and EOPT) or who voluntarily register for VAT. They must issue VAT Sales Invoices and charge 12% output VAT on taxable sales.
- **Non-VAT (Percentage Tax) Businesses:** Taxpayers whose gross annual sales/receipts do not exceed the ₱3,000,000 threshold and who are not VAT-registered. They issue Non-VAT Invoices/Receipts and are subject to Percentage Tax (e.g., 3% under Sec. 116) instead of VAT.
- **Branch Consistency:** Per BIR registration rules (RR No. 7-2024, RR No. 15-2024), a taxpayer's branches operate under the same legal taxpayer identity (Head Office TIN with a 3-digit branch code). A branch cannot independently choose a tax type inconsistent with the parent taxpayer entity (e.g., a VAT-registered corporation's branch cannot operate as Non-VAT).

---

## 3. Statutory Discount Requirements & Calculation Formulas

### A. Senior Citizen (SC) & Person with Disability (PWD) Discounts (20% + VAT Exemption)
Governed by RA 9994 / RR 7-2010 and RA 10754 / RR 5-2017.
1. **Eligibility:** Must present valid OSCA Senior Citizen ID or PWD ID (NCDA / PDAO).
2. **Exclusivity:** Applies **only** to goods and services exclusively consumed, used, or enjoyed by the SC or PWD.
3. **VAT Treatment:** The sale is **VAT-Exempt**. Establishments are strictly precluded from billing VAT to SC/PWD.
4. **Official Mathematical Formulas (Established under RR 7-2010 and RR 5-2017):**
   - **Step 1: Determine VAT-Exclusive Gross Selling Price (GSP)**
     $$\text{GSP} = \frac{\text{Gross Selling Price Inclusive of VAT}}{1.12}$$
   - **Step 2: Calculate 20% Statutory Discount**
     $$\text{Discount Amount} = \text{GSP} \times 0.20$$
   - **Step 3: Calculate Total Amount Due (Net Payable)**
     $$\text{Net Payable} = \text{GSP} - \text{Discount Amount} = \text{GSP} \times 0.80$$
   - **Establishment Tax Deduction (Itemized Deduction):** The allowable deduction from gross income is the **actual 20% discount granted** (which equals $\text{GSP} \times 0.20$). Under RR 7-2010 and RR 5-2017, the gross sales recorded for income tax purposes must reflect the undiscounted VAT-exclusive selling price ($\text{GSP}$), while the discount is booked separately as an ordinary and necessary expense (Discount Expense).

#### Concrete Example: Restaurant Dine-In (SC/PWD)
- Gross Displayed Menu Price (Inclusive of 12% VAT): **₱1,120.00**
- **Step 1 (VAT Removal):** $\text{GSP} = \frac{1,120.00}{1.12} = \mathbf{₱1,000.00}$ (This is the VAT-Exempt sales base).
- **Step 2 (20% Discount):** $\text{Discount} = 1,000.00 \times 0.20 = \mathbf{₱200.00}$.
- **Step 3 (Net Payable):** $\text{Net Payable} = 1,000.00 - 200.00 = \mathbf{₱800.00}$.
- **Establishment Accounting:**
  - Cash / Receivable (Debit): **₱800.00**
  - SC/PWD Discount Expense (Debit): **₱200.00**
  - Sales (Credit): **₱1,000.00**

---

### B. Special 5% Basic Necessities and Prime Commodities (BNPC) Discounts
- Governed by Joint Administrative Order (JAO) No. 2, Series of 2008 (DTI, DA, DOE, DOH, DILG) pursuant to the Price Act (RA 7581) and RA 9994 / RA 10754.
- **Scope:** Applies to senior citizens and PWDs purchasing specified basic necessities (e.g., canned sardines, milk, coffee, bread) and prime commodities (e.g., flour, processed meat, soap) at a special **5% discount**.
- **Formula:** 
  $$\text{Discount Amount} = \text{Retail Price} \times 0.05$$
  $$\text{Net Payable} = \text{Retail Price} \times 0.95$$
- *Note:* Basic necessities and prime commodities in their original state are generally VAT-exempt under NIRC Sec. 109(A), hence no VAT extraction step is required for raw agricultural/marine items. For processed items subject to VAT, VAT exemption applies alongside the 5% discount following standard VAT-removal logic.

---

### C. Promotional and Employee Discounts
1. **Promotional Discounts:** Granted at the point of sale (e.g., storewide promo sales, membership cards). Under RR 16-2005 (Sec. 4.106-9), discounts determined and granted at the time of sale and expressly indicated in the invoice are excluded from gross selling price for VAT purposes.
2. **Employee Discounts:** Granted as a fringe benefit or corporate perk. Taxable as a fringe benefit under NIRC Sec. 33 (Fringe Benefit Tax) if granted to managerial/supervisory employees, or treated as compensation/tax-exempt de minimis benefits if compliant with RR 2-98 (as amended) thresholds. For POS calculation, employee discounts reduce the gross selling price at the time of sale.

---

### D. Discount Stacking and Mixed Transactions
1. **Double Discount Prohibition:** Under RR 7-2010 (Sec. 6) and RR 5-2017 (Sec. 6), **no double discounts shall be allowed**. If a good or service is subject to a promotional discount that exceeds 20%, the senior citizen or PWD shall avail of the promotional discount or the statutory 20% discount, **whichever is higher**. The two cannot be added together (stacked).
2. **Mixed Dining Transactions (Companions):** When an SC/PWD dines with non-eligible companions, the order **must be processed separately as an independent transaction** or explicitly segregated on the bill. The 20% discount and VAT exemption apply **strictly** to the food and beverages exclusively consumed by the SC/PWD. Non-eligible companions' shares remain subject to standard 12% VAT and full pricing.

---

## 4. Invoice Requirements & Withholding Tax Forms

### A. EOPT Invoicing Requirements (RR No. 7-2024 & RR No. 6-2026)
- **Unified Document:** Under RA 11976 (EOPT Act) and RR 7-2024, the **Sales Invoice** is the primary legal document evidencing the sale of both goods and services (replacing the traditional separate Official Receipt for services).
- **Mandatory Information (Sec. 6(B), RR 7-2024 as amended by RR 6-2026):**
  1. Registered name and business address of the seller.
  2. TIN followed by the 5-digit branch code.
  3. Date of transaction, quantity, unit cost, and description of goods/services.
  4. Gross sales breakdown: **Vatable Sales**, **VAT-exempt Sales**, **Zero-rated Sales**, and **VAT Amount**.
  5. **New Mandate under RR No. 6-2026 (Effective Oct 16, 2026):** The amount of **Creditable Withholding Tax (CWT)**, if applicable, must be shown as a separate item (Item 22) on the invoice. Unused invoices may continue to be used until fully consumed provided the phrase *"Less: Withholding Tax"* is stamped conspicuously on the face.

### B. Distinction: BIR Form 2306 vs. BIR Form 2307
- **Important Classification Rule:** **Forms 2306 and 2307 are NOT discounts.** They are official BIR Certificates of Withholding Tax issued by income/tax payors to income payees.
- **BIR Form 2306 (Certificate of Final Withholding Tax):** Issued to document final withholding taxes withheld at source on passive incomes or specific final tax regimes (e.g., final tax on certain royalties, prizes, or interest).
- **BIR Form 2307 (Certificate of Creditable Withholding Tax At Source):** Issued by a withholding agent (customer/client) to certify that creditable withholding tax (CWT) has been withheld from payments made to suppliers/vendors for income tax purposes, or withholding VAT where applicable. The amount withheld acts as a tax credit that the payee can apply against its income tax liability upon filing quarterly/annual income tax returns.

---

## 5. Unresolved Regulatory Questions & Accountant Validation
1. **POS Z-Counter Segregation:** Automated POS terminals must maintain separate Z-read counters and subsidiary ledgers for SC/PWD VAT-exempt sales, regular VATable sales, and zero-rated sales to withstand BIR audit scrutiny.
2. **Digital ID Verification:** While physical IDs are traditionally mandated, integration with NCDA/OSCA digital verification registries requires formal corporate validation with the company's external auditor and RDO.
3. **Accountant Sign-off Required:** Before deploying discount calculation algorithms to production, the localized tax mapping, VAT-removal rounding rules (two decimal places), and general ledger account mappings must be signed off by a certified public accountant (CPA) accredited by the BOA/BIR.
