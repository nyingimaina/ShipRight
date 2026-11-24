import { render } from '@testing-library/react';
import Home from '@/pages/index';

const replace = jest.fn();
const mockRouter = { pathname: '/', replace };
jest.mock('next/router', () => ({ useRouter: () => mockRouter }));

describe('Home', () => {
  beforeEach(() => replace.mockClear());

  it('sends the root page to projects', () => {
    mockRouter.pathname = '/';
    render(<Home />);
    expect(replace).toHaveBeenCalledWith('/projects');
  });

  it('never hijacks a mis-served deep link', () => {
    mockRouter.pathname = '/servers/';
    render(<Home />);
    expect(replace).not.toHaveBeenCalled();
  });
});
