import { useEffect } from 'react';
import { useRouter } from 'next/router';

export default function Home() {
  const router = useRouter();
  // Only the actual home route redirects. A mis-served deep link must never be silently turned
  // into /projects — that made a broken static fallback look like a navigation bug.
  useEffect(() => { if (router.pathname === '/') router.replace('/projects'); }, [router.pathname]);
  return null;
}
