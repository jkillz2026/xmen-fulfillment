'use client';
import type { AgentEvent } from '../hooks/useAgentHub';

/**
 * Client-side simulation of the ASP.NET agent pipeline for the hosted demo.
 * Emits the same AgentStarted / AgentCompleted / PipelineComplete shapes the
 * real SignalR hub emits, with deterministic fraud routing so different
 * orders exercise different paths (approve / manual review / auto-reject).
 */

export interface DemoOrderBody {
  orderId: string;
  customerId: string;
  customerEmail: string;
  items: { sku: string; name: string; quantity: number; unitPrice: number }[];
  shippingAddress: { line1: string; city: string; state: string; zip: string; country: string };
  paymentMethodId: string;
}

type Listener = (e: AgentEvent) => void;
const listeners = new Set<Listener>();
const pendingReview = new Map<string, DemoOrderBody>();

export function onEvent(cb: Listener): () => void {
  listeners.add(cb);
  return () => listeners.delete(cb);
}

type EventInput =
  | Omit<Extract<AgentEvent, { type: 'started' }>, 'timestamp'>
  | Omit<Extract<AgentEvent, { type: 'completed' }>, 'timestamp'>
  | Omit<Extract<AgentEvent, { type: 'pipeline' }>, 'timestamp'>;

function emit(e: EventInput) {
  const evt = { ...e, timestamp: new Date().toISOString() } as AgentEvent;
  listeners.forEach(cb => cb(evt));
}

const sleep = (ms: number) => new Promise(r => setTimeout(r, ms));

const DISPOSABLE_DOMAINS = ['mailinator', 'tempmail', 'guerrillamail', '10minutemail'];

function fraudScore(order: DemoOrderBody, total: number): number {
  const hash = [...order.orderId].reduce((a, c) => (a * 31 + c.charCodeAt(0)) >>> 0, 7);
  let score = 10 + (hash % 45);
  if (total > 1000) score += 40;
  const domain = order.customerEmail.split('@')[1]?.toLowerCase() ?? '';
  if (DISPOSABLE_DOMAINS.some(d => domain.includes(d))) score += 35;
  if (order.shippingAddress.country !== 'US') score += 15;
  return Math.min(score, 98);
}

async function agent(orderId: string, name: string, task: string,
                     action: string, summary: string, success = true) {
  emit({ type: 'started', orderId, agentName: name, task });
  await sleep(900 + Math.random() * 700);
  emit({ type: 'completed', orderId, agentName: name, action, summary, success });
  return success;
}

async function runFulfillment(order: DemoOrderBody, total: number) {
  if (order.items[0].sku === 'X-003') {
    await agent(order.orderId, 'Wolverine', 'Reserve inventory',
      'reserve-inventory', 'SKU X-003 is out of stock — cannot reserve', false);
    emit({ type: 'pipeline', orderId: order.orderId, status: 'Failed' });
    return;
  }

  await agent(order.orderId, 'Wolverine', 'Reserve inventory',
    'reserve-inventory', `Reserved ${order.items[0].quantity} × ${order.items[0].sku} (${order.items[0].name}) — Warehouse East`);

  const payFail = order.paymentMethodId.startsWith('pm_fail')
    ? 'Card declined — authorization failed'
    : order.paymentMethodId.startsWith('pm_capture_fail')
      ? `Authorized $${total.toFixed(2)} — capture failed, voiding auth`
      : null;

  if (payFail !== null) {
    await agent(order.orderId, 'Gambit', 'Authorize & capture payment',
      'capture-payment', payFail, false);
    await agent(order.orderId, 'Wolverine', 'Release reserved inventory',
      'compensate', 'Saga compensation: released reserved inventory');
    emit({ type: 'pipeline', orderId: order.orderId, status: 'Failed' });
    return;
  }

  await agent(order.orderId, 'Gambit', 'Authorize & capture payment',
    'capture-payment', `Authorized + captured $${total.toFixed(2)} on ${order.paymentMethodId}`);

  await agent(order.orderId, 'Storm', 'Rate & book shipment',
    'create-label', `Cheapest rate: USPS Ground $4.20, 3-day — label LB-${Math.floor(10000 + Math.random() * 89999)}`);

  await agent(order.orderId, 'Jean Grey', 'Send order confirmation',
    'send-email', `Confirmation email sent to ${order.customerEmail}`);

  emit({ type: 'pipeline', orderId: order.orderId, status: 'Completed' });
}

export async function submitOrder(order: DemoOrderBody) {
  const total = order.items.reduce((s, i) => s + i.unitPrice * i.quantity, 0);

  await agent(order.orderId, 'Cerebro', 'Orchestrate fulfillment pipeline',
    'orchestrate', `Decomposed order ${order.orderId} into 6 agent tasks`);

  if (!await agent(order.orderId, 'Cyclops', 'Validate order payload',
    'validate', `Schema valid — ${order.items.length} item(s), total $${total.toFixed(2)}`)) {
    emit({ type: 'pipeline', orderId: order.orderId, status: 'Failed' });
    return;
  }

  const score = fraudScore(order, total);
  const band = score < 30 ? 'low' : score < 70 ? 'moderate' : 'high';
  await agent(order.orderId, 'Beast', 'Score fraud risk (0–100)',
    'score-risk', `Fraud score: ${score}/100 — ${band} risk`, score < 70);
  if (score >= 70) {
    emit({ type: 'pipeline', orderId: order.orderId, status: 'Failed' });
    return;
  }

  if (score >= 30) {
    pendingReview.set(order.orderId, order);
    emit({ type: 'pipeline', orderId: order.orderId, status: 'AwaitingApproval' });
    return;
  }

  await runFulfillment(order, total);
}

export async function reviewOrder(orderId: string, action: 'approve' | 'reject') {
  const order = pendingReview.get(orderId);
  if (!order) return;
  pendingReview.delete(orderId);

  if (action === 'reject') {
    await agent(orderId, 'Cerebro', 'Apply manual review decision',
      'reject-order', `Order ${orderId} rejected by human review`);
    emit({ type: 'pipeline', orderId, status: 'Failed' });
    return;
  }

  await agent(orderId, 'Cerebro', 'Apply manual review decision',
    'resume', `Order ${orderId} approved — resuming pipeline`);
  const total = order.items.reduce((s, i) => s + i.unitPrice * i.quantity, 0);
  await runFulfillment(order, total);
}
