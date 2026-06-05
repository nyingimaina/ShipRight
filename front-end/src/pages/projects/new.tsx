import { useEffect } from 'react';
import { useRouter } from 'next/router';

// Project creation is now a side pane on the Projects page
export default function NewProjectRedirect() {
  const router = useRouter();
  useEffect(() => { router.replace('/projects/'); }, []);
  return null;
}
