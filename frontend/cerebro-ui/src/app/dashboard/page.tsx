'use client';
import { useState, useCallback, useRef } from 'react';
import { useAgentHub, AgentEvent } from '../../hooks/useAgentHub';

const AGENT_META: Record<string, { icon: string; color: string }> = {
  Cerebro:     { icon: '🧠', color: 'text-purple-400' },
  Cyclops:     { icon: '👁️',  color: 'text-red-400'    },
  Beast:       { icon: '🧬', color: 'text-blue-400'   },
  Wolverine:   { icon: '⚡', color: 'text-yellow-400' },
  Gambit:      { icon: '🃏', color: 'text-pink-400'   },
  Storm:       { icon: '⛈️', color: 'text-sky-400'    },
  'Jean Grey': { icon: '🔮', color: 'text-rose-400'   },
};

type FeedEntry = AgentEvent & { id: number };

const API = process.env.NEXT_PUBLIC_API_URL ?? 'http://localhost:5016';

const DEFAULT_FORM = {
  orderId: '',
  customerId: 'CUST-01',
  customerEmail: 'jean.grey@xmen.com',
  sku: 'X-001',
  itemName: 'Visor',
  quantity: '1',
  unitPrice: '49.99',
  line1: '1407 Graymalkin Ln',
  city: 'Salem Center',
  state: 'NY',
  zip: '10560',
  country: 'US',
  paymentMethodId: 'pm_ok_test',
};

type FormKey = keyof typeof DEFAULT_FORM;

export default function Dashboard() {
  const [form, setForm] = useState(DEFAULT_FORM);
  const [feed, setFeed] = useState<FeedEntry[]>([]);
  const [submitting, setSubmitting] = useState(false);
  const [reviewing, setReviewing] = useState(false);
  const [pipelineStatus, setPipelineStatus] = useState<string | null>(null);
  const [activeOrderId, setActiveOrderId] = useState<string | null>(null);
  const counterRef = useRef(0);

  const addEntry = useCallback((e: AgentEvent) => {
    setFeed(prev => {
      if (e.type === 'completed') {
        // Replace the matching 'started' card so the agent row transitions
        // from "Running…" to "✓ Done" / "✗ Failed" in place.
        const idx = prev.findIndex(
          entry => entry.type === 'started' && 'agentName' in entry && entry.agentName === e.agentName
        );
        if (idx !== -1) {
          const updated = [...prev];
          updated[idx] = { ...e, id: prev[idx].id };
          return updated;
        }
      }
      // started, pipeline, or unmatched completed → prepend a new card
      return [{ ...e, id: counterRef.current++ }, ...prev];
    });
    if (e.type === 'pipeline') setPipelineStatus((e as Extract<AgentEvent, { type: 'pipeline' }>).status);
  }, []);

  const { connected } = useAgentHub(addEntry);

  async function handleSubmit(ev: React.FormEvent) {
    ev.preventDefault();
    setFeed([]);
    setPipelineStatus(null);
    setSubmitting(true);

    const orderId = form.orderId.trim() || `ORD-${Date.now()}`;
    setActiveOrderId(orderId);
    const body = {
      orderId,
      customerId: form.customerId,
      customerEmail: form.customerEmail,
      items: [{
        sku: form.sku,
        name: form.itemName,
        quantity: Number(form.quantity),
        unitPrice: Number(form.unitPrice),
      }],
      shippingAddress: {
        line1: form.line1,
        city: form.city,
        state: form.state,
        zip: form.zip,
        country: form.country,
      },
      paymentMethodId: form.paymentMethodId,
    };

    try {
      await fetch(`${API}/orders`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(body),
      });
    } catch (err) {
      console.error('Order submit error:', err);
    } finally {
      setSubmitting(false);
    }
  }

  function field(label: string, key: FormKey, placeholder?: string) {
    return (
      <div>
        <label className="block text-xs text-slate-400 mb-0.5">{label}</label>
        <input
          className="w-full bg-slate-800 border border-slate-600 rounded px-2 py-1 text-sm text-white focus:outline-none focus:border-purple-500 transition-colors"
          value={form[key]}
          onChange={e => setForm(f => ({ ...f, [key]: e.target.value }))}
          placeholder={placeholder}
        />
      </div>
    );
  }

  async function handleReview(action: 'approve' | 'reject') {
    if (!activeOrderId) return;
    setReviewing(true);
    try {
      await fetch(`${API}/orders/${activeOrderId}/${action}`, { method: 'POST' });
    } catch (err) {
      console.error(err);
    } finally {
      setReviewing(false);
    }
  }

  const statusBadge =
    pipelineStatus === 'Completed'       ? 'bg-emerald-600' :
    pipelineStatus === 'Failed'          ? 'bg-red-600'     :
    pipelineStatus === 'AwaitingApproval'? 'bg-yellow-500 text-black' : ''

  return (
    <div className="min-h-screen bg-slate-950 text-white flex flex-col">

      {/* ── Header ── */}
      <header className="border-b border-slate-800 px-6 py-3 flex items-center justify-between shrink-0">
        <div className="flex items-center gap-3">
          <span className="text-2xl">🧠</span>
          <div>
            <h1 className="font-bold text-lg leading-none tracking-wide">CEREBRO</h1>
            <p className="text-xs text-slate-400">X-Men Fulfillment Command Center</p>
          </div>
        </div>
        <div className="flex items-center gap-2 text-xs">
          <span className={`w-2 h-2 rounded-full ${connected ? 'bg-emerald-400' : 'bg-red-500'}`} />
          <span className="text-slate-400">{connected ? 'Live' : 'Disconnected'}</span>
        </div>
      </header>

      <div className="flex flex-1 overflow-hidden">

        {/* ── Order Form ── */}
        <aside className="w-80 shrink-0 border-r border-slate-800 p-4 overflow-y-auto">
          <h2 className="text-xs font-semibold text-slate-400 uppercase tracking-widest mb-3">
            Submit Order
          </h2>
          <form onSubmit={handleSubmit} className="flex flex-col gap-2">
            {field('Order ID (auto-generated if blank)', 'orderId', 'ORD-...')}
            {field('Customer ID', 'customerId')}
            {field('Customer Email', 'customerEmail')}

            <div className="border-t border-slate-700 pt-2 mt-1">
              <p className="text-xs text-slate-500 mb-2 font-medium">Line Item</p>
              {field('SKU', 'sku')}
              {field('Item Name', 'itemName')}
              <div className="grid grid-cols-2 gap-2 mt-2">
                {field('Qty', 'quantity')}
                {field('Unit Price ($)', 'unitPrice')}
              </div>
            </div>

            <div className="border-t border-slate-700 pt-2 mt-1">
              <p className="text-xs text-slate-500 mb-2 font-medium">Shipping Address</p>
              {field('Street', 'line1')}
              <div className="grid grid-cols-2 gap-2 mt-2">
                {field('City', 'city')}
                {field('State', 'state')}
              </div>
              <div className="grid grid-cols-2 gap-2 mt-2">
                {field('ZIP', 'zip')}
                {field('Country', 'country')}
              </div>
            </div>

            <div className="border-t border-slate-700 pt-2 mt-1">
              <p className="text-xs text-slate-500 mb-2 font-medium">Payment</p>
              {field('Payment Method ID', 'paymentMethodId')}
              <div className="mt-1 text-xs text-slate-600 space-y-0.5">
                <p><code className="text-slate-500">pm_ok_*</code> → success</p>
                <p><code className="text-slate-500">pm_fail_*</code> → card declined</p>
                <p><code className="text-slate-500">pm_capture_fail_*</code> → compensation</p>
              </div>
            </div>

            <button
              type="submit"
              disabled={submitting || !connected}
              className="mt-2 bg-purple-600 hover:bg-purple-500 disabled:bg-slate-700 disabled:text-slate-500 rounded-lg py-2 text-sm font-semibold transition-colors cursor-pointer disabled:cursor-not-allowed"
            >
              {submitting ? 'Processing…' : '⚡ Submit Order'}
            </button>
          </form>
        </aside>

        {/* ── Agent Feed ── */}
        <main className="flex-1 p-4 overflow-y-auto">
          <div className="flex items-center justify-between mb-3">
            <h2 className="text-xs font-semibold text-slate-400 uppercase tracking-widest">
              Agent Activity
            </h2>
            <div className="flex items-center gap-2">
            {pipelineStatus && (
              <span className={`text-xs font-bold px-3 py-1 rounded-full ${statusBadge}`}>
                {pipelineStatus}
              </span>
            )}
            {pipelineStatus === 'AwaitingApproval' && (
              <>
                <button
                  onClick={() => handleReview('approve')}
                  disabled={reviewing}
                  className="text-xs font-semibold px-3 py-1 rounded-full bg-emerald-700 hover:bg-emerald-600 disabled:opacity-50 transition-colors cursor-pointer"
                >
                  ✅ Approve
                </button>
                <button
                  onClick={() => handleReview('reject')}
                  disabled={reviewing}
                  className="text-xs font-semibold px-3 py-1 rounded-full bg-red-700 hover:bg-red-600 disabled:opacity-50 transition-colors cursor-pointer"
                >
                  ❌ Reject
                </button>
              </>
            )}
          </div>
          </div>

          {feed.length === 0 && (
            <div className="h-64 flex items-center justify-center text-slate-600 text-sm">
              Submit an order to watch the agents work in real time.
            </div>
          )}

          <div className="flex flex-col gap-2">
            {feed.map(entry => {
              const agentName = 'agentName' in entry ? entry.agentName : '';
              const meta = AGENT_META[agentName ?? ''] ?? { icon: '🤖', color: 'text-slate-400' };
              const ts = entry.timestamp?.slice(11, 19) ?? '';

              if (entry.type === 'started') {
                return (
                  <div key={entry.id} className="flex items-start gap-3 p-3 rounded-lg bg-slate-900 border border-yellow-900/50">
                    <span className="text-xl leading-none">{meta.icon}</span>
                    <div className="flex-1 min-w-0">
                      <div className="flex items-center gap-2">
                        <span className={`text-sm font-semibold ${meta.color}`}>{entry.agentName}</span>
                        <span className="text-xs bg-yellow-900 text-yellow-300 px-1.5 py-0.5 rounded animate-pulse">
                          Running…
                        </span>
                      </div>
                      <p className="text-xs text-slate-500 mt-0.5 truncate">{entry.task}</p>
                    </div>
                    <span className="text-xs text-slate-600 shrink-0">{ts}</span>
                  </div>
                );
              }

              if (entry.type === 'completed') {
                const ok = (entry as Extract<AgentEvent, { type: 'completed' }>).success;
                return (
                  <div key={entry.id} className={`flex items-start gap-3 p-3 rounded-lg bg-slate-900 border ${ok ? 'border-slate-700' : 'border-red-800'}`}>
                    <span className="text-xl leading-none">{meta.icon}</span>
                    <div className="flex-1 min-w-0">
                      <div className="flex items-center gap-2">
                        <span className={`text-sm font-semibold ${meta.color}`}>{entry.agentName}</span>
                        <span className={`text-xs px-1.5 py-0.5 rounded ${ok ? 'bg-emerald-900 text-emerald-300' : 'bg-red-900 text-red-300'}`}>
                          {ok ? '✓ Done' : '✗ Failed'}
                        </span>
                      </div>
                      <p className="text-xs text-slate-400 mt-0.5 leading-relaxed">
                        {(entry as Extract<AgentEvent, { type: 'completed' }>).summary}
                      </p>
                    </div>
                    <span className="text-xs text-slate-600 shrink-0">{ts}</span>
                  </div>
                );
              }

              if (entry.type === 'pipeline') {
                const st = (entry as Extract<AgentEvent, { type: 'pipeline' }>).status;
                const bannerClass =
                  st === 'Completed'        ? 'border-emerald-700 bg-emerald-950 text-emerald-300' :
                  st === 'Failed'           ? 'border-red-700 bg-red-950 text-red-300'             :
                  st === 'AwaitingApproval' ? 'border-yellow-700 bg-yellow-950 text-yellow-300'    :
                                              'border-slate-700 bg-slate-900 text-slate-300';
                return (
                  <div key={entry.id} className={`p-3 rounded-lg border ${bannerClass} text-center text-sm font-bold`}>
                    Pipeline {st}
                  </div>
                );
              }

              return null;
            })}
          </div>
        </main>
      </div>
    </div>
  );
}
