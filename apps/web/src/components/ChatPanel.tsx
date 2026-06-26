/// Placeholder for the M3 cross-note chat. Mounted only when the right panel is expanded.
export default function ChatPanel() {
  return (
    <div className="flex h-full flex-col items-center justify-center p-6 text-center">
      <p className="text-sm font-medium text-gray-600 dark:text-gray-300">Chat</p>
      <p className="mt-1 text-xs text-gray-400 dark:text-gray-500">
        Ask questions across your recordings. Coming in a later milestone.
      </p>
    </div>
  );
}
