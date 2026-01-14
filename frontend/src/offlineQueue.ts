import { get, set } from "idb-keyval";
import api from "./api";

export type QueueItem = {
  id: string;
  type: "scan";
  payload: any;
  url: string;
  status: "pending" | "failed" | "done";
  error?: string;
};

const KEY = "tsd-queue";

export async function getQueue(): Promise<QueueItem[]> {
  return ((await get(KEY)) as QueueItem[]) || [];
}

export async function addToQueue(item: QueueItem) {
  const queue = await getQueue();
  queue.push(item);
  await set(KEY, queue);
}

export async function updateQueue(queue: QueueItem[]) {
  await set(KEY, queue);
}

export async function syncQueue(setProgress?: (n: number) => void) {
  const queue = await getQueue();
  let processed = 0;
  for (const item of queue) {
    if (item.status === "failed") {
      processed += 1;
      continue;
    }
    try {
      await api.post(item.url, item.payload);
      item.status = "done" as any;
      processed += 1;
      if (setProgress) setProgress(processed / queue.length);
    } catch (e: any) {
      item.status = "failed";
      item.error = e?.response?.data?.detail || "Ошибка";
    }
  }
  const remaining = queue.filter((q) => q.status !== "done");
  await updateQueue(remaining);
  return remaining;
}
