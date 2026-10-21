import { useNavigate } from 'react-router';
import { t } from '../../shared/i18n/i18n';
import { Button } from '../../shared/ui';
import { useCanApprove, usePendingApprovals } from './useApprovals';

/** "Approvals 2" in the top bar, for users who can approve anything. */
export function ApprovalsButton() {
  const navigate = useNavigate();
  const canApprove = useCanApprove();
  const { waiting } = usePendingApprovals();
  if (!canApprove) return null;
  return (
    <Button
      variant="ghost-dark"
      onClick={() => navigate('/pos/approvals')}
      aria-label={t('approval.buttonLabel', { count: waiting.length })}
    >
      {t('approval.button')}
      {waiting.length > 0 && <span className="approvals-badge">{waiting.length}</span>}
    </Button>
  );
}
