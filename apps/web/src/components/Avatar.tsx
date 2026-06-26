/// A round initials bubble for the signed-in user.
export default function Avatar({ initials }: { initials: string }) {
  return (
    <span className="flex h-8 w-8 items-center justify-center rounded-full bg-gray-900 text-xs font-medium text-white dark:bg-gray-100 dark:text-gray-900">
      {initials}
    </span>
  );
}
