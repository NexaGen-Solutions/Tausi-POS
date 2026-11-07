# M-Pesa in Tausi POS (from 1.1)

With M-Pesa set up, a bar can:

- **Send a PIN prompt** to the customer's phone from the settle screen, or from the order screen if
  waiters may.
- **Print a "Scan to pay" QR** on every bill.
- **See payments made to its till** on the settle screen, to add to a bill.

The cashier still presses **Complete** for every bill. Without M-Pesa set up, everything works as in 1.0:
cashiers type the code from the customer's SMS.

## What the business needs from Safaricom

1. **An M-Pesa till (Buy Goods) or a Paybill** in the business's name. For Buy Goods, Safaricom's letter
   gives two numbers: the **till number** (what customers pay to) and the **store number** (the head office
   number). Prompts to phones are sent from the store number and paid into the till.
2. **A Daraja account** at [developer.safaricom.co.ke](https://developer.safaricom.co.ke), and an app with
   **M-Pesa Express (STK push)**, **Dynamic QR**, and **C2B**. The app gives the **consumer key** and
   **consumer secret**.
3. **Going live:** on the Daraja portal, choose **Go Live** for the app. You confirm the till or paybill
   with an OTP to the business's M-Pesa administrator phone. Safaricom then emails the live **passkey** for
   STK push. Until then, use the **sandbox** credentials and Safaricom's test numbers.
4. **The relay API key** from NexaGen Solutions (see [RELAY.md](RELAY.md)).

## Settings → M-Pesa (admin site)

Open **Settings → M-Pesa**. You need the **Settings** permission.

| Field                                                  | What to type                                                    |
| ------------------------------------------------------ | --------------------------------------------------------------- |
| **Type**                                               | **Buy Goods (Till)** or **Paybill**                             |
| **Till number** and **Store number**                   | Buy Goods: both, from Safaricom's letter                        |
| **Paybill number**                                     | Paybill: customers type the ticket number as the account number |
| **Consumer key**, **Consumer secret**, **STK passkey** | From the Daraja app (the passkey from the Go Live email)        |
| **Environment**                                        | **Sandbox** while testing, **Live** for real money              |
| **Relay address** and **Relay API key**                | `https://pay.nexagensolutions.com` and the key from NexaGen     |

The secrets are stored encrypted and are never shown again: they show as `••••` with **Change**. After a
restore on another PC they can't be read, and the page asks for them again.

Press **Save changes**, then **Test connection**. It checks three things, each marked ✓ or with a plain
error:

1. **Daraja:** the consumer key and secret work.
2. **Relay:** the API key works.
3. **Payments to the till:** the relay's addresses are registered with Safaricom. This happens the first
   time, and again whenever they change.

**Rules** (all on by default):

- **Print "Scan to pay" QR on bills.** If Safaricom doesn't answer within 3 seconds, the bill prints
  without the QR, with just the till line.
- **Send prompt to phone (STK push).**
- **Waiters can send prompts from the order screen.**
- **Allow typing a code when offline.** Typed codes are checked against Safaricom's copy when it arrives.
  A typed code with no copy after 24 hours shows as **Typed, never confirmed**, which may be a fake SMS.

**Connection** shows "Connected to the Tausi relay", "Not connected (no open tickets)", or "Can't reach
the relay" with the reason. The server connects only while tickets are open. It also collects when the
day opens, just before **Close day**, and when **Fetch payments now** is pressed. Payments made while it
wasn't connected wait on the relay and are never lost.

## Day to day

- **Settle → M-Pesa → Send prompt to phone:** type the phone number. The amount is what's left; it can be
  lowered to split the payment. Press **Send prompt**. The customer enters their PIN, and the payment is
  added to the bill. Then press **Complete**. If nothing happens within 2 minutes, send it again or take
  another payment.
- **Settle → M-Pesa → Payments received:** payments to the till that aren't on a bill yet. One made by
  scanning this bill's QR is marked **QR on this bill**. One for the same amount is only a suggestion:
  check the name with the customer. A payment bigger than the bill is used only up to the bill's amount;
  the rest stays for another bill. M-Pesa never gives change.
- **Admin site → M-Pesa payments:** everything received, applied, unapplied, not from the POS, and typed
  but never confirmed. **Apply to ticket** puts an unapplied payment on an open bill. **Mark as not from
  POS** is for money that wasn't for a bill, such as an owner's deposit; it posts the money to **Other
  income** or **Owner's capital**. **Export to Excel** lets you check against the Safaricom till
  statement.
- **Close day** warns about unapplied M-Pesa payments, but still closes.

## In the books

An M-Pesa payment goes into the books when its bill is settled, as in 1.0: debit M-Pesa, credit sales.
A payment that isn't on any bill is **not** in the books until it is applied and settled, or marked as
not from the POS. Unapplied payments are listed on Close day and in the M-Pesa payments list.
