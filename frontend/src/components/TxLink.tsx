import { shortHash, txUrl } from "../lib/format";

/** Arc-explorer link for an on-chain action. */
export default function TxLink({ hash, label }: { hash: string; label?: string }) {
  return (
    <a href={txUrl(hash)} target="_blank" rel="noreferrer" className="num font-semibold text-steel-300 hover:text-steel-400">
      {label ?? shortHash(hash)} ↗
    </a>
  );
}
