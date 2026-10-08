'use client';
import * as demo from './demoEngine';

/**
 * Hybrid transport: when NEXT_PUBLIC_SIGNALR_URL + NEXT_PUBLIC_API_URL are set
 * at build time the app talks to the real ASP.NET Core backend over SignalR +
 * REST. When they're absent (the static Netlify demo build) it runs an
 * in-browser simulation of the same event contract.
 */
export const LIVE = Boolean(
  process.env.NEXT_PUBLIC_API_URL && process.env.NEXT_PUBLIC_SIGNALR_URL
);

const API = process.env.NEXT_PUBLIC_API_URL ?? 'http://localhost:5016';

export async function submitOrder(body: demo.DemoOrderBody) {
  if (LIVE) {
    const res = await fetch(`${API}/orders`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(body),
    });
    if (!res.ok) throw new Error(`Order submit failed: ${res.status}`);
    return;
  }
  await demo.submitOrder(body);
}

export async function reviewOrder(orderId: string, action: 'approve' | 'reject') {
  if (LIVE) {
    const res = await fetch(`${API}/orders/${orderId}/${action}`, { method: 'POST' });
    if (!res.ok) throw new Error(`Review failed: ${res.status}`);
    return;
  }
  await demo.reviewOrder(orderId, action);
}
