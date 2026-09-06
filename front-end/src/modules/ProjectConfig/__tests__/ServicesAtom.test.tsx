import { render, screen, fireEvent } from '@testing-library/react';
import ServicesAtom from '../atoms/ServicesAtom';
import { ServiceDraft, emptyServiceDraft } from '../atoms/types';

jest.mock('@/shared/ApiService', () => ({
  api: { post: jest.fn() },
}));

jest.mock('jattac.libs.web.zest-button', () => ({
  __esModule: true,
  default: ({ children, onClick, disabled }: any) => (
    <button type="button" onClick={onClick} disabled={disabled}>{children}</button>
  ),
}));

jest.mock('jattac.libs.web.zest-textbox', () => ({
  __esModule: true,
  default: ({ value, onChange, placeholder }: any) => (
    <input value={value} onChange={onChange} placeholder={placeholder} />
  ),
}));

const ecr = { id: 'reg-ecr', name: 'prod ecr', registry: '123.dkr.ecr.us-east-1.amazonaws.com', username: '', tags: ['aws', 'prod'], authType: 'AwsEcr', createdAt: '', modifiedAt: '' } as any;
const ghcr = { id: 'reg-ghcr', name: 'company ghcr', registry: 'ghcr.io', username: 'bot', tags: ['github'], createdAt: '', modifiedAt: '' } as any;

function svc(overrides: Partial<ServiceDraft> = {}): ServiceDraft {
  return { ...emptyServiceDraft(), ...overrides };
}

describe('ServicesAtom registry picker', () => {
  it('renders saved registry resource dropdown', () => {
    render(
      <ServicesAtom
        services={[svc()]}
        composeNames={[]}
        registries={[ecr, ghcr]}
        errors={{}}
        onServicesChange={jest.fn()}
        onRegistriesChange={jest.fn()}
      />
    );
    const options = screen.getAllByRole('option');
    expect(options.some(o => o.textContent === 'prod ecr (AWS ECR)')).toBe(true);
    expect(options.some(o => o.textContent === 'company ghcr')).toBe(true);
  });

  it('filters registries by selected tag (exclusive)', () => {
    render(
      <ServicesAtom
        services={[svc()]}
        composeNames={[]}
        registries={[ecr, ghcr]}
        errors={{}}
        onServicesChange={jest.fn()}
        onRegistriesChange={jest.fn()}
      />
    );
    const filterSelects = screen.getAllByRole('combobox');
    // last combobox is the tag filter
    const filter = filterSelects[filterSelects.length - 1];
    fireEvent.change(filter, { target: { value: 'aws' } });

    const registrySelect = filterSelects[0];
    const options = Array.from(registrySelect.querySelectorAll('option')).map(o => o.textContent);
    expect(options).toContain('prod ecr (AWS ECR)');
    expect(options).not.toContain('company ghcr');
  });

  it('applies registry host when a resource is selected', () => {
    const onChange = jest.fn();
    render(
      <ServicesAtom
        services={[svc()]}
        composeNames={[]}
        registries={[ecr, ghcr]}
        errors={{}}
        onServicesChange={onChange}
        onRegistriesChange={jest.fn()}
      />
    );
    const registrySelect = screen.getAllByRole('combobox')[0];
    fireEvent.change(registrySelect, { target: { value: 'reg-ecr' } });
    expect(onChange).toHaveBeenCalledWith([
      expect.objectContaining({
        dockerRegistryResourceId: 'reg-ecr',
        dockerRegistry: '123.dkr.ecr.us-east-1.amazonaws.com',
      }),
    ]);
  });
});