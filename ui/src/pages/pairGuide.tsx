import { PairSteps } from '../components/common';
import { post } from '../host';
import { Icon } from '../icons';
import type { Page } from './page';

export const createPairGuide = (): Page => {
  const el = (
    <div class="wp-page">
      <div class="wp-empty">
        <PairSteps />
        <div class="wp-empty__actions">
          <button
            type="button"
            class="wp-btn wp-btn--primary wp-btn--lg wp-btn--block"
            onclick={() => post({ type: 'openAddDevice' })}
          >
            <Icon name="bluetooth" />
            <span l:text="pair.addDevice" />
          </button>
        </div>
      </div>
    </div>
  ) as HTMLDivElement;

  return { el, sections: {} };
};
