import { useState, type FormEvent } from "react";
import { useStore } from "../lib/store";

export default function Login() {
  const { login } = useStore();
  const [email, setEmail] = useState("");
  const [busy, setBusy] = useState(false);

  const submit = async (e: FormEvent) => {
    e.preventDefault();
    if (!email.includes("@")) return;
    setBusy(true);
    try {
      await login(email);
    } finally {
      setBusy(false);
    }
  };

  return (
    <div className="flex min-h-screen items-center justify-center px-6">
      <div className="w-full max-w-md animate-rise">
        <div className="mb-10 flex flex-col items-center text-center">
          <img src="/brand/happening-mark-square-padded-transparent.png" alt="Happening" className="h-16 w-16" />
          <h1 className="mt-5 font-display text-4xl font-semibold tracking-tight text-paper-100">
            Happening <span className="prism-text">RFM</span>
          </h1>
          <p className="mt-3 max-w-sm text-sm leading-relaxed text-ink-300">
            Prediction markets born from committed institutional demand. Sealed quotes, escrowed bonds,
            and a market that opens already funded and priced.
          </p>
        </div>

        <form onSubmit={submit} className="panel p-6">
          <label htmlFor="email" className="label-caps">
            Sign in with email
          </label>
          <input
            id="email"
            type="email"
            required
            autoFocus
            value={email}
            onChange={(e) => setEmail(e.target.value)}
            placeholder="desk@institution.com"
            className="input mt-2"
          />
          <button type="submit" disabled={busy} className="btn-gold mt-4 w-full">
            {busy ? "Creating your wallet…" : "Continue"}
          </button>
          <p className="mt-4 text-center text-xs leading-relaxed text-ink-400">
            A Circle smart wallet is created for you behind this email. No seed phrase, no gas.
          </p>
        </form>

        <p className="mt-6 text-center text-[11px] text-ink-500">
          Built on Arc · USDC-native settlement · Gas Station sponsored
        </p>
      </div>
    </div>
  );
}
