'use client';
import { useEffect, useRef, useState } from 'react';
import * as signalR from '@microsoft/signalr';

export type AgentEvent =
  | { type: 'started';   orderId: string; agentName: string; task: string;    timestamp: string }
  | { type: 'completed'; orderId: string; agentName: string; action: string; summary: string; success: boolean; timestamp: string }
  | { type: 'pipeline';  orderId: string; status: string; timestamp: string };

export function useAgentHub(onEvent: (e: AgentEvent) => void) {
  const [connected, setConnected] = useState(false);
  const callbackRef = useRef(onEvent);
  callbackRef.current = onEvent;

  useEffect(() => {
    const url = process.env.NEXT_PUBLIC_SIGNALR_URL ?? 'http://localhost:5016/hubs/agents';

    const conn = new signalR.HubConnectionBuilder()
      .withUrl(url)
      .withAutomaticReconnect()
      .configureLogging(signalR.LogLevel.Warning)
      .build();

    conn.on('AgentStarted',    (d) => callbackRef.current({ type: 'started',   ...d }));
    conn.on('AgentCompleted',  (d) => callbackRef.current({ type: 'completed', ...d }));
    conn.on('PipelineComplete',(d) => callbackRef.current({ type: 'pipeline',  ...d }));

    conn.start().then(() => setConnected(true)).catch(console.error);
    conn.onreconnected(() => setConnected(true));
    conn.onclose(() => setConnected(false));

    return () => { conn.stop(); };
  }, []);

  return { connected };
}
